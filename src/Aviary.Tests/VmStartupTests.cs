using Aviary.Core;
using Xunit;

namespace Aviary.Tests;

public sealed class VmStartupTests
{
    [Theory]
    [InlineData(VmEngine.HyperV, "open,start")]
    [InlineData(VmEngine.Qemu, "start,open")]
    public async Task StartupOrdersConsoleForItsBackend(VmEngine engine, string expected)
    {
        var actions = new List<string>();
        await VmStartup.StartOrOpenAsync(new() { Engine = engine }, VmStatus.Stopped,
            () => { actions.Add("start"); return Task.CompletedTask; }, () => { actions.Add("open"); return Task.CompletedTask; });
        Assert.Equal(expected, string.Join(",", actions));
    }
    [Fact]
    public async Task NativeMachineDoesNotStartWhenConsoleFails()
    {
        bool started = false;
        await Assert.ThrowsAsync<IOException>(() => VmStartup.StartOrOpenAsync(new() { Engine = VmEngine.HyperV }, VmStatus.Stopped,
            () => { started = true; return Task.CompletedTask; }, () => throw new IOException("Console unavailable")));
        Assert.False(started);
    }
    [Fact]
    public async Task OpeningRunningMachineDoesNotRestartIt()
    {
        bool opened = false;
        await VmStartup.StartOrOpenAsync(new() { Engine = VmEngine.HyperV }, VmStatus.Running,
            () => throw new InvalidOperationException("Already running"), () => { opened = true; return Task.CompletedTask; });
        Assert.True(opened);
    }
}
