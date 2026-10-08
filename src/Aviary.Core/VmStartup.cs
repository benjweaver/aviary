namespace Aviary.Core;

public static class VmStartup
{
    public static async Task StartOrOpenAsync(VmConfiguration vm, VmStatus status, Func<Task> start, Func<Task> open)
    {
        bool needsStart = status is VmStatus.Stopped or VmStatus.Error;
        if (vm.Engine == VmEngine.HyperV)
        {
            // The console must be ready before firmware starts its DVD key prompt.
            await open();
            if (needsStart) await start();
        }
        else
        {
            if (needsStart) await start();
            await open();
        }
    }
}
