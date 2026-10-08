using System.Text.Json;
using Aviary.Core;
using Aviary.HyperV;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Xunit;

namespace Aviary.Tests;

public sealed class HyperVTests
{
    [Fact]
    public void ExistingConfigurationsStayOnQemu()
    {
        var vm = VmStore.Deserialize("""{"name":"Existing Linux"}""");
        Assert.Equal(VmEngine.Qemu, vm.Engine); Assert.Null(vm.HyperVId);
        Assert.Throws<InvalidDataException>(() => (vm with { Engine = VmEngine.HyperV }).Validate());
    }
    [Theory]
    [InlineData("Running", VmStatus.Running)]
    [InlineData("Off", VmStatus.Stopped)]
    [InlineData("Saved", VmStatus.Paused)]
    [InlineData("Paused", VmStatus.Paused)]
    [InlineData("Starting", VmStatus.Starting)]
    [InlineData("Saving", VmStatus.Stopping)]
    [InlineData("Critical", VmStatus.Error)]
    public void WindowsStatesMapWithoutPretendingUnknownMachinesAreStopped(string state, VmStatus expected) => Assert.Equal(expected, HyperVBackend.MapState(state));

    [Fact]
    public async Task NativeCreationPersistenceAndPowerOperationsUseRegisteredIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "aviary-hyperv-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var iso = Path.Combine(root, "guest.iso"); await File.WriteAllTextAsync(iso, "fixture");
            var store = new VmStore(Path.Combine(root, "Machines")); var commands = new FakeCommands();
            await using var backend = new HyperVBackend(store, new("X64", 8, 32768, true, "Windows"), commands);
            var vm = await backend.CreateAsync(new() { Engine = VmEngine.HyperV, DiskFormat = DiskFormat.Vhdx, IsoPath = iso, HyperVSwitch = "Default Switch" });
            Assert.Equal(commands.NativeId, vm.HyperVId);
            Assert.EndsWith("system.vhdx", vm.DiskPath);
            Assert.Equal(vm, Assert.Single((await store.LoadAsync()).Vms));
            await backend.StartAsync(vm); Assert.Equal(VmStatus.Running, backend.GetStatus(vm.Id).Status);
            await backend.PauseAsync(vm.Id); Assert.Equal(VmStatus.Paused, backend.GetStatus(vm.Id).Status);
            await backend.ResumeAsync(vm.Id); Assert.Equal(VmStatus.Running, backend.GetStatus(vm.Id).Status);
            await backend.StopAsync(vm.Id); Assert.Equal(VmStatus.Stopped, backend.GetStatus(vm.Id).Status);
            Assert.All(commands.Identities, id => Assert.Equal(commands.NativeId, id));
            var before = commands.Calls; await backend.DisposeAsync(); Assert.Equal(before, commands.Calls);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ProbeFailureDisablesNativeBackendWithoutBlockingQemuLibrary()
    {
        var available = await HyperVBackend.ProbeAsync(new FailingCommands());
        Assert.False(available.Available); Assert.Contains("permission", available.Message);
        await using var router = new MachineBackend(null, null, [new() { Engine = VmEngine.HyperV, DiskFormat = DiskFormat.Vhdx }]);
        Assert.False(router.Available(VmEngine.HyperV));
    }
    [Fact]
    public async Task PowershellArgumentsStayDataAndEveryScriptParses()
    {
        var runner = new HyperVCommands();
        const string name = "Guest '; throw 'injected'; # $(Get-Process) ` Unicode 日本語";
        Assert.Equal(name, await runner.RunAsync("[Console]::Write($p.Name)", new { Name = name }));
        foreach (var field in typeof(HyperVScripts).GetFields())
        {
            await runner.RunAsync("$tokens=$null; $errors=$null; [System.Management.Automation.Language.Parser]::ParseInput($p.Script,[ref]$tokens,[ref]$errors) | Out-Null; if ($errors.Count) { throw ($errors | Out-String) }", new { Script = field.GetValue(null) });
        }
    }
    [Fact]
    public async Task OwnershipMismatchStopsAnOperationBeforeMutation()
    {
        var runner = new HyperVCommands();
        var fakeHost = "function Import-Module {} ; function Get-VM { [pscustomobject]@{ Notes='not-aviary' } }; ";
        var error = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(fakeHost + HyperVScripts.Require + "\nthrow 'mutation reached'", new { Id = Guid.NewGuid(), HyperVId = Guid.NewGuid() }));
        Assert.Contains("does not match", error.Message); Assert.DoesNotContain("mutation reached", error.Message);
    }
    sealed class FailingCommands : IHyperVCommands
    {
        public Task<string> RunAsync(string script, object arguments, CancellationToken token = default) => throw new IOException("No management permission.");
    }
    sealed class FakeCommands : IHyperVCommands
    {
        public Guid NativeId { get; } = Guid.NewGuid();
        public List<Guid> Identities { get; } = [];
        public int Calls { get; private set; }
        Guid id; string state = "Off";
        public async Task<string> RunAsync(string script, object arguments, CancellationToken token = default)
        {
            Calls++; var data = JsonSerializer.SerializeToElement(arguments);
            if (script == HyperVScripts.Create)
            {
                id = data.GetProperty("Id").GetGuid(); await File.WriteAllTextAsync(data.GetProperty("DiskPath").GetString()!, "fixture", token);
                return JsonSerializer.Serialize(new { Id = NativeId });
            }
            if (script == HyperVScripts.Status) return JsonSerializer.Serialize(new[] { new { Id = id, State = state, Error = (string?)null } });
            Identities.Add(data.GetProperty("HyperVId").GetGuid());
            state = script == HyperVScripts.Pause ? "Paused" : script == HyperVScripts.Shutdown || script == HyperVScripts.ForceOff ? "Off" : "Running";
            return "";
        }
    }
}
