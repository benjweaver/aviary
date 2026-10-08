using Aviary.Core;
using Aviary.HyperV;

namespace Aviary.Qemu;

public sealed class MachineBackend(QemuBackend? qemu, HyperVBackend? hyperV, IEnumerable<VmConfiguration> configurations) : IVirtualMachineBackend, IAsyncDisposable
{
    readonly Dictionary<Guid, VmEngine> engines = configurations.ToDictionary(v => v.Id, v => v.Engine);
    public event Action<VmState>? StateChanged
    {
        add { if (qemu is not null) qemu.StateChanged += value; if (hyperV is not null) hyperV.StateChanged += value; }
        remove { if (qemu is not null) qemu.StateChanged -= value; if (hyperV is not null) hyperV.StateChanged -= value; }
    }
    public bool Available(VmEngine engine) => engine == VmEngine.HyperV ? hyperV is not null : qemu is not null;
    IVirtualMachineBackend For(VmEngine engine) => engine == VmEngine.HyperV
        ? hyperV ?? throw new InvalidOperationException("Hyper-V is unavailable. See Settings for host requirements.")
        : qemu ?? throw new InvalidOperationException("Choose a QEMU installation in Settings.");
    IVirtualMachineBackend For(Guid id) => For(engines.GetValueOrDefault(id));
    public VmState GetStatus(Guid id) => !Available(engines.GetValueOrDefault(id)) ? new(id, VmStatus.Error, "This machine's backend is unavailable. See Settings.") : For(id).GetStatus(id);
    public async Task<VmConfiguration> CreateAsync(VmConfiguration vm, CancellationToken token = default)
    { var created = await For(vm.Engine).CreateAsync(vm, token); engines[created.Id] = created.Engine; return created; }
    public Task StartAsync(VmConfiguration vm, CancellationToken token = default) { engines[vm.Id] = vm.Engine; return For(vm.Engine).StartAsync(vm, token); }
    public Task StopAsync(Guid id, CancellationToken token = default) => For(id).StopAsync(id, token);
    public Task ForceStopAsync(Guid id, CancellationToken token = default) => For(id).ForceStopAsync(id, token);
    public Task PauseAsync(Guid id, CancellationToken token = default) => For(id).PauseAsync(id, token);
    public Task ResumeAsync(Guid id, CancellationToken token = default) => For(id).ResumeAsync(id, token);
    public Task ResetAsync(Guid id, CancellationToken token = default) => For(id).ResetAsync(id, token);
    public IDisplayConnection GetDisplay(Guid id) => engines.GetValueOrDefault(id) == VmEngine.Qemu && qemu is not null ? qemu.GetDisplay(id) : throw new InvalidOperationException("Hyper-V uses Windows Virtual Machine Connection.");
    public Task EjectSetupMediaAsync(VmConfiguration vm) => vm.Engine == VmEngine.HyperV && hyperV is not null ? hyperV.EjectSetupMediaAsync(vm) : Task.CompletedTask;
    public Task OpenNativeConsoleAsync(Guid id) => hyperV is not null && engines.GetValueOrDefault(id) == VmEngine.HyperV ? hyperV.OpenConsoleAsync(id) : throw new InvalidOperationException("Hyper-V is unavailable.");
    public async ValueTask DisposeAsync() { if (hyperV is not null) await hyperV.DisposeAsync(); if (qemu is not null) await qemu.DisposeAsync(); }
}
