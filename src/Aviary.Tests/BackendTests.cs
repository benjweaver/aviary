using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Aviary.Qmp;
using Xunit;
namespace Aviary.Tests;

public sealed class ConfigurationTests
{
    [Fact] public void RoundTripPreservesTypedConfiguration() { var vm = new VmConfiguration { Name = "Ubuntu", DiskPath = @"C:\VMs\disk.qcow2" }; Assert.Equal(vm, VmStore.Deserialize(JsonSerializer.Serialize(vm, VmStore.Json))); }
    [Fact] public void LegacySchemaMigrates() { var vm = VmStore.Deserialize("{\"name\":\"Legacy\"}"); Assert.Equal(1, vm.SchemaVersion); }
    [Fact] public void FutureSchemaIsRejected() { Assert.Throws<InvalidDataException>(() => VmStore.Deserialize("{\"schemaVersion\":42}")); }
    [Theory][InlineData(0, 1024)][InlineData(1, 0)][InlineData(257, 512)] public void InvalidHardwareIsRejected(int cpu, int ram) { Assert.Throws<InvalidDataException>(() => new VmConfiguration { CpuCores = cpu, MemoryMB = ram }.Validate()); }
    [Fact] public async Task AtomicSaveAndReload() { var root = Path.Combine(Path.GetTempPath(), "aviary-test-" + Guid.NewGuid()); try { var store = new VmStore(root); var vm = new VmConfiguration(); await store.SaveAsync(vm); await store.SaveAsync(vm with { Name = "Updated" }); var loaded = await store.LoadAsync(); Assert.Empty(loaded.Errors); Assert.Equal("Updated", Assert.Single(loaded.Vms).Name); Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories)); } finally { Directory.Delete(root, true); } }
    [Fact] public async Task RemovePreservesDisk() { var root = Path.Combine(Path.GetTempPath(), "aviary-test-" + Guid.NewGuid()); try { var store = new VmStore(root); var vm = new VmConfiguration(); await store.SaveAsync(vm); var disk = Path.Combine(store.DirectoryFor(vm.Id), "disk.qcow2"); await File.WriteAllTextAsync(disk, "data"); store.RemoveFromLibrary(vm.Id); Assert.True(File.Exists(disk)); Assert.Empty((await store.LoadAsync()).Vms); } finally { Directory.Delete(root, true); } }
}
public sealed class CommandTests
{
    static readonly HostCapabilities Host = new("X64", 8, 16384, true, "Windows");
    static readonly QemuInstallation Qemu = new(@"C:\QEMU", "test", Enum.GetValues<GuestArchitecture>());
    static VmConfiguration Vm => new() { DiskPath = @"C:\VMs\a,b\system.qcow2", IsoPath = @"C:\ISO files\Linux.iso" };
    [Fact] public void UsesStructuredArgumentsAndJsonFilenames() { var info = QemuCommandBuilder.Build(Vm with { Name = "name & calc.exe" }, Qemu, Host, 4500, 5901); Assert.False(info.UseShellExecute); Assert.Contains("name & calc.exe", info.ArgumentList); var args = info.ArgumentList.ToList(); var block = JsonDocument.Parse(args[args.IndexOf("-blockdev") + 1]); Assert.Equal(Vm.DiskPath, block.RootElement.GetProperty("file").GetProperty("filename").GetString()); Assert.Contains("127.0.0.1:1", args); Assert.Contains("none", args); }
    [Theory][InlineData(Acceleration.Whpx, "whpx")][InlineData(Acceleration.Tcg, "tcg")] public void SelectsRequestedAcceleration(Acceleration acceleration, string argument) { Assert.Contains(argument, QemuCommandBuilder.Build(Vm with { Acceleration = acceleration }, Qemu, Host, 4500, 5901).ArgumentList); }
    [Fact] public void NeverSilentlyFallsBackFromWhpx() { Assert.Throws<InvalidOperationException>(() => QemuCommandBuilder.Build(Vm with { Acceleration = Acceleration.Whpx }, Qemu, Host with { WhpxAvailable = false }, 4500, 5901)); }
    [Theory][InlineData(GuestArchitecture.Arm64)][InlineData(GuestArchitecture.RiscV64)] public void UnsupportedGuestsAreExplicit(GuestArchitecture architecture) { Assert.Throws<NotSupportedException>(() => QemuCommandBuilder.Build(Vm with { Architecture = architecture }, Qemu, Host, 4500, 5901)); }
    [Fact] public void X86UsesMatchingBinary() { Assert.EndsWith("qemu-system-i386.exe", QemuCommandBuilder.Build(Vm with { Architecture = GuestArchitecture.X86 }, Qemu, Host, 4500, 5901).FileName); }
    [Fact] public void HostResourceReservationIsEnforced() { Assert.Throws<InvalidDataException>(() => HostProbe.ValidateAllocation(Vm with { MemoryMB = 14000 }, Host)); Assert.Throws<InvalidDataException>(() => HostProbe.ValidateAllocation(Vm with { CpuCores = 8 }, Host)); }
    [Fact] public void MissingArchitectureIsRejected() { Assert.Throws<InvalidOperationException>(() => QemuCommandBuilder.Build(Vm, Qemu with { Architectures = [] }, Host, 4500, 5901)); }
    [Fact] public void DisabledNetworkIsExplicit() { var args = QemuCommandBuilder.Build(Vm with { NetworkEnabled = false, IsoPath = "" }, Qemu, Host, 4500, 5901).ArgumentList.ToList(); Assert.Equal("none", args[args.IndexOf("-nic") + 1]); Assert.DoesNotContain("-boot", args); }
    [Fact] public void RawDiskUsesRawDriver() { var args = QemuCommandBuilder.Build(Vm with { DiskFormat = DiskFormat.Raw }, Qemu, Host, 4500, 5901).ArgumentList.ToList(); Assert.Contains("\"driver\":\"raw\"", args[args.IndexOf("-blockdev") + 1]); }
}
public sealed class QmpTests
{
    [Fact] public void RecognizesEvents() { using var doc = JsonDocument.Parse("{\"event\":\"STOP\"}"); Assert.True(QmpClient.IsEvent(doc.RootElement)); }
    [Fact]
    public async Task HandshakeCorrelatesOutOfOrderResponsesAndEvents()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; var server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token); using var reader = new StreamReader(peer.GetStream()); await using var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true }; await writer.WriteLineAsync("{\"QMP\":{\"version\":{}}}");
                using var hello = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!); Assert.Equal("qmp_capabilities", hello.RootElement.GetProperty("execute").GetString()); Assert.Equal(JsonValueKind.Object, hello.RootElement.GetProperty("arguments").ValueKind); await writer.WriteLineAsync(JsonSerializer.Serialize(new { @return = new { }, id = hello.RootElement.GetProperty("id").GetInt64() }));
                using var first = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!); using var second = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!); await writer.WriteLineAsync("{\"event\":\"STOP\"}");
                foreach (var command in new[] { second, first }) await writer.WriteLineAsync(JsonSerializer.Serialize(new { @return = new { command = command.RootElement.GetProperty("execute").GetString() }, id = command.RootElement.GetProperty("id").GetInt64() }));
            }, timeout.Token);
            await using var client = new QmpClient(); var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); client.EventReceived += (name, _) => { if (name == "STOP") stopped.TrySetResult(); }; await client.ConnectAsync(port, timeout.Token); var first = client.ExecuteAsync("query-status", token: timeout.Token); var second = client.ExecuteAsync("query-version", token: timeout.Token); Assert.Equal("query-status", (await first).GetProperty("command").GetString()); Assert.Equal("query-version", (await second).GetProperty("command").GetString()); await stopped.Task.WaitAsync(timeout.Token); await server;
        }
        finally { listener.Stop(); }
    }
    [Fact] public async Task DisconnectFailsPendingRequest() { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); try { var server = Task.Run(async () => { using var peer = await listener.AcceptTcpClientAsync(timeout.Token); using var reader = new StreamReader(peer.GetStream()); await using var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true }; await writer.WriteLineAsync("{\"QMP\":{}}"); using var hello = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!); await writer.WriteLineAsync(JsonSerializer.Serialize(new { @return = new { }, id = hello.RootElement.GetProperty("id").GetInt64() })); await reader.ReadLineAsync(timeout.Token); }, timeout.Token); await using var client = new QmpClient(); await client.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token); await Assert.ThrowsAnyAsync<Exception>(() => client.ExecuteAsync("query-status", token: timeout.Token)); await server; } finally { listener.Stop(); } }
}

