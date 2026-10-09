using Aviary.HyperV;
using Xunit;
namespace Aviary.Tests;

public sealed class GpuPartitionTests
{
    public static TheoryData<string, string> Scripts => new()
    {
        { nameof(HyperVScripts.PartitionableGpus), HyperVScripts.PartitionableGpus },
        { nameof(HyperVScripts.GpuDriverCopy), HyperVScripts.GpuDriverCopy },
        { nameof(HyperVScripts.GpuAttach), HyperVScripts.GpuAttach },
        { nameof(HyperVScripts.GpuDetach), HyperVScripts.GpuDetach },
        { nameof(HyperVScripts.ShutdownAndWait), HyperVScripts.ShutdownAndWait },
        { nameof(HyperVScripts.GpuAdapterCount), HyperVScripts.GpuAdapterCount },
        { nameof(HyperVScripts.GpuStagingInstall), HyperVScripts.GpuStagingInstall },
    };

    [Fact]
    public void DriverFilesAreStagedWhereTheGuestMovesThemFrom()
    {
        Assert.DoesNotContain(@"'C:\Windows\", HyperVScripts.GpuDriverCopy);
        Assert.Contains("$p.Staging", HyperVScripts.GpuDriverCopy);
        Assert.Contains($"'{HyperVScripts.GpuStagingRoot}'", HyperVScripts.GpuStagingInstall);
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task GpuScriptsParse(string name, string script)
    {
        Assert.False(string.IsNullOrWhiteSpace(name));
        await new HyperVCommands().RunAsync("$t=$null;$e=$null;[System.Management.Automation.Language.Parser]::ParseInput($p.Script,[ref]$t,[ref]$e)|Out-Null;if($e.Count){throw ($e|Out-String)}", new { Script = script });
    }

    [Fact]
    public void AttachSavesWhatDetachRestores()
    {
        foreach (var setting in new[] { "Cache", "Low", "High", "StopAction", "CheckpointType", "AutomaticCheckpoints" })
        {
            Assert.Contains(setting + " =", HyperVScripts.GpuAttach);
            Assert.Contains("$b." + setting, HyperVScripts.GpuDetach);
        }
    }
}