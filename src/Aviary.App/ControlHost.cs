using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Microsoft.UI.Dispatching;

namespace Aviary.App;

// Gives the control channel (aviary-mcp) the app's library. Calls arrive on pipe threads, so the machine list is
// served from a snapshot and every change is applied on the UI thread, where the library is owned.
sealed class AppLibrary : IMachineLibrary
{
    readonly LibraryViewModel model;
    readonly DispatcherQueue ui;
    readonly Action<VmConfiguration> changed;
    volatile VmConfiguration[] snapshot;

    public AppLibrary(LibraryViewModel model, DispatcherQueue ui, Action<VmConfiguration> changed)
    {
        this.model = model; this.ui = ui; this.changed = changed;
        snapshot = [.. model.Machines];
        model.Machines.CollectionChanged += (_, _) => snapshot = [.. model.Machines];
    }

    public IReadOnlyList<VmConfiguration> Machines => snapshot;
    public VmStore Store => model.Store;
    public MachineBackend Backend => model.Backend ?? throw new InvalidOperationException("Aviary has no virtualization engine configured. Open Settings in Aviary.");

    public Task SaveAsync(VmConfiguration updated)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ui.TryEnqueue(async () =>
        {
            try
            {
                await model.Store.SaveAsync(updated);
                var index = model.Machines.ToList().FindIndex(m => m.Id == updated.Id);
                if (index >= 0) model.Machines[index] = updated;
                changed(updated);
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
        })) done.TrySetException(new InvalidOperationException("Aviary is closing."));
        return done.Task;
    }
}
