using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Aviary.Core;
using Aviary.Infrastructure;

namespace Aviary.HyperV;

public sealed record HyperVAvailability(bool Available, string Message, string[] Switches);

public sealed class HyperVBackend(VmStore store, HostCapabilities host, IHyperVCommands commands) : IVirtualMachineBackend, IAsyncDisposable
{
    readonly ConcurrentDictionary<Guid, VmConfiguration> machines = new();
    readonly ConcurrentDictionary<Guid, VmState> states = new();
    readonly SemaphoreSlim gate = new(1);
    readonly CancellationTokenSource lifetime = new();
    Task? monitor;
    int disposed;
    public event Action<VmState>? StateChanged;
    public static async Task<HyperVAvailability> ProbeAsync(IHyperVCommands commands)
    {
        try
        {
            using var json = JsonDocument.Parse(await commands.RunAsync(HyperVScripts.Probe, new { }));
            var switches = json.RootElement.GetProperty("Switches").EnumerateArray().Select(s => s.GetString()!).ToArray();
            return new(true, "Hyper-V is ready. Guest windows open in Windows Virtual Machine Connection.", switches);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or JsonException)
        { return new(false, ex.Message + " Your account also needs Hyper-V management permission.", []); }
    }
    public async Task InitializeAsync(IEnumerable<VmConfiguration> configurations)
    {
        foreach (var vm in configurations.Where(v => v.Engine == VmEngine.HyperV)) machines[vm.Id] = vm;
        await RefreshAsync(); monitor = MonitorAsync();
    }
    public VmState GetStatus(Guid id) => states.GetValueOrDefault(id) ?? new(id, VmStatus.Stopped);
    void Set(Guid id, VmStatus status, string? error = null)
    {
        var next = new VmState(id, status, error);
        if (states.TryGetValue(id, out var previous) && previous == next) return;
        states[id] = next; StateChanged?.Invoke(next);
    }
    public static VmStatus MapState(string state) => state switch
    {
        "Running" => VmStatus.Running, "Off" => VmStatus.Stopped, "Paused" or "Saved" => VmStatus.Paused,
        "Starting" or "Resuming" or "Restoring" => VmStatus.Starting,
        "Stopping" or "Saving" or "Pausing" => VmStatus.Stopping, _ => VmStatus.Error
    };
    static object Identity(VmConfiguration vm) => new { vm.Id, vm.HyperVId };
    VmConfiguration Require(Guid id) => machines.TryGetValue(id, out var vm) && vm.HyperVId is not null && vm.HyperVId != Guid.Empty ? vm : throw new InvalidOperationException("This machine has no Hyper-V registration on this PC.");
    public async Task<VmConfiguration> CreateAsync(VmConfiguration configuration, CancellationToken token = default)
    {
        HostProbe.ValidateAllocation(configuration, host);
        if (configuration.Engine != VmEngine.HyperV) throw new InvalidDataException("Choose the Hyper-V backend.");
        if (configuration.NetworkEnabled && string.IsNullOrWhiteSpace(configuration.HyperVSwitch)) throw new InvalidDataException("Choose an existing Hyper-V virtual switch, or disconnect networking.");
        if (!File.Exists(configuration.IsoPath)) throw new FileNotFoundException("Choose an existing installer ISO.");
        await gate.WaitAsync(token);
        try
        {
            var directory = store.DirectoryFor(configuration.Id);
            if (Directory.Exists(directory)) throw new IOException("This machine directory already exists.");
            Directory.CreateDirectory(Path.Combine(directory, "disks"));
            var vm = await GuestProvisioning.PrepareAsync(configuration with { DiskPath = Path.Combine(directory, "disks", "system.vhdx"), DynamicDisplay = false }, directory, token);
            using var result = JsonDocument.Parse(await commands.RunAsync(HyperVScripts.Create, new
            {
                vm.Id, Name = vm.Name, vm.CpuCores, vm.MemoryMB, vm.DiskGB, vm.DiskPath, vm.IsoPath, vm.SetupIsoPath,
                Directory = directory, vm.NetworkEnabled, Switch = vm.HyperVSwitch, Windows = vm.OperatingSystem == "Windows"
            }, token));
            vm = vm with { HyperVId = result.RootElement.GetProperty("Id").GetGuid() };
            if (vm.HyperVId == Guid.Empty) throw new IOException("Hyper-V did not return a valid machine ID.");
            try { await store.SaveAsync(vm, token); }
            catch { await commands.RunAsync(HyperVScripts.RemoveRegistration, Identity(vm), CancellationToken.None); throw; }
            machines[vm.Id] = vm; Set(vm.Id, VmStatus.Stopped); return vm;
        }
        finally { gate.Release(); }
    }
    public async Task StartAsync(VmConfiguration configuration, CancellationToken token = default)
    {
        configuration.Validate();
        if (!File.Exists(configuration.DiskPath)) throw new FileNotFoundException("The Hyper-V disk is missing. Moving native Hyper-V machines requires export/import in Hyper-V Manager.");
        if (configuration.IsoPath.Length > 0 && !File.Exists(configuration.IsoPath)) throw new FileNotFoundException("The installer ISO is missing. Clear or replace it in Edit configuration.");
        if (configuration.SetupIsoPath.Length > 0 && !File.Exists(configuration.SetupIsoPath)) throw new FileNotFoundException("The setup CD is missing. Eject setup media in this machine's menu.");
        if (configuration.SshEnabled) await GuestProvisioning.WriteSshConfigAsync(configuration, store.DirectoryFor(configuration.Id));
        machines[configuration.Id] = configuration; var vm = Require(configuration.Id);
        await ExecuteAsync(vm.Id, HyperVScripts.Start, new { vm.Id, vm.HyperVId, vm.Name, vm.DiskPath, vm.IsoPath, vm.SetupIsoPath, ManagedSetupIsoPath = Path.Combine(GuestProvisioning.AccessDirectory(store.DirectoryFor(vm.Id)), "setup.iso") }, token);
    }
    async Task ExecuteAsync(Guid id, string script, object arguments, CancellationToken token)
    {
        Require(id); await gate.WaitAsync(token);
        try
        {
            if (script == HyperVScripts.Start || script == HyperVScripts.Resume || script == HyperVScripts.Reset) Set(id, VmStatus.Starting);
            else Set(id, VmStatus.Stopping);
            await commands.RunAsync(script, arguments, token); await RefreshCoreAsync(token);
        }
        catch (Exception ex) { Set(id, VmStatus.Error, ex.Message); throw; }
        finally { gate.Release(); }
    }
    public Task StopAsync(Guid id, CancellationToken token = default) => ExecuteAsync(id, HyperVScripts.Shutdown, Identity(Require(id)), token);
    public Task ForceStopAsync(Guid id, CancellationToken token = default) => ExecuteAsync(id, HyperVScripts.ForceOff, Identity(Require(id)), token);
    public Task PauseAsync(Guid id, CancellationToken token = default) => ExecuteAsync(id, HyperVScripts.Pause, Identity(Require(id)), token);
    public Task ResumeAsync(Guid id, CancellationToken token = default) => ExecuteAsync(id, HyperVScripts.Resume, Identity(Require(id)), token);
    public Task ResetAsync(Guid id, CancellationToken token = default) => ExecuteAsync(id, HyperVScripts.Reset, Identity(Require(id)), token);
    public Task EjectSetupMediaAsync(VmConfiguration vm) => commands.RunAsync(HyperVScripts.Require + "\nif ($vm.State -ne 'Off') { throw 'Shut down before ejecting setup media.' }; Get-VMDvdDrive -VM $vm | Where-Object { $_.Path -eq $p.SetupIsoPath -and $_.ControllerLocation -eq 2 } | Set-VMDvdDrive -Path $null", new { vm.Id, vm.HyperVId, vm.SetupIsoPath });
    public async Task OpenConsoleAsync(Guid id)
    {
        var vm = Require(id); await commands.RunAsync(HyperVScripts.Require, Identity(vm));
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "vmconnect.exe")) { UseShellExecute = false };
        foreach (var arg in new[] { "localhost", "-G", vm.HyperVId!.Value.ToString("D") }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Windows Virtual Machine Connection could not open.");
        await Task.Run(() =>
        {
            if (!process.WaitForInputIdle(15000)) throw new IOException("VMConnect did not become ready. The machine was not automatically started; open its console and start it there.");
        });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            process.Refresh();
            if (process.HasExited) throw new IOException("VMConnect closed before its window was ready. Open the console again to start this machine.");
            if (process.MainWindowHandle != IntPtr.Zero) break;
            if (DateTime.UtcNow >= deadline) throw new IOException("VMConnect has not shown its window. Start the machine from that window once it appears.");
            await Task.Delay(100);
        }
    }
    public async Task RefreshAsync()
    {
        await gate.WaitAsync(lifetime.Token);
        try { await RefreshCoreAsync(lifetime.Token); }
        catch (Exception ex) when (ex is IOException or JsonException or System.ComponentModel.Win32Exception || ex is OperationCanceledException && !lifetime.IsCancellationRequested) { foreach (var id in machines.Keys) Set(id, VmStatus.Error, ex.Message); }
        finally { gate.Release(); }
    }
    async Task RefreshCoreAsync(CancellationToken token)
    {
        if (machines.IsEmpty) return;
        var registered = machines.Values.Where(v => v.HyperVId is not null && v.HyperVId != Guid.Empty).ToArray();
        foreach (var vm in machines.Values.Where(v => v.HyperVId is null || v.HyperVId == Guid.Empty)) Set(vm.Id, VmStatus.Error, "Hyper-V registration is missing.");
        if (registered.Length == 0) return;
        using var json = JsonDocument.Parse(await commands.RunAsync(HyperVScripts.Status, new { Machines = registered.Select(Identity).ToArray() }, token));
        foreach (var row in json.RootElement.EnumerateArray())
        {
            var id = row.GetProperty("Id").GetGuid(); if (!machines.ContainsKey(id)) continue;
            string state = row.GetProperty("State").GetString()!;
            Set(id, MapState(state), row.GetProperty("Error").GetString() ?? (MapState(state) == VmStatus.Error ? "Hyper-V state: " + state : null));
        }
    }
    async Task MonitorAsync()
    {
        try { while (!lifetime.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token); await RefreshAsync(); } }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); if (monitor is not null) await monitor;
        // Native Hyper-V guests belong to Windows and continue after Aviary closes.
        lifetime.Dispose(); gate.Dispose();
    }
}
