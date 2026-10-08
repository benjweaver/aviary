using Aviary.HyperV;
using Xunit;
namespace Aviary.Tests;

public sealed class GpuSetupTests
{
    static object Arguments(string path = "") => new { Script = Path.Combine(AppContext.BaseDirectory, "setup-hyperv-gpu.ps1"), State = path };
    const string Load = """
        $tokens=$null;$errors=$null
        [System.Management.Automation.Language.Parser]::ParseFile($p.Script,[ref]$tokens,[ref]$errors)|Out-Null
        if($errors.Count){throw ($errors|Out-String)}
        . $p.Script -VmId '11111111-1111-1111-1111-111111111111' -AviaryId '22222222-2222-2222-2222-222222222222'
        """ + "\n";
    [Fact]
    public async Task HelperRejectsUnrelatedVmBeforeChangingHardware()
    {
        var result = await new HyperVCommands().RunAsync(Load + """
            function Get-VM { param($Id) [pscustomobject]@{Notes='Someone else'} }
            try { Get-OwnedVm; throw 'accepted unrelated VM' } catch {
                if($_.Exception.Message -notlike '*does not belong*'){throw}
            }
            'rejected'
            """, Arguments());
        Assert.EndsWith("rejected", result);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardwareFailureRestoresOriginalSettingsAndRemovesOnlySelectedGpu(bool failAtStart)
    {
        string directory = Path.Combine(Path.GetTempPath(), "aviary-gpu-test-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            string state = Path.Combine(directory, "recovery.json");
            var result = await new HyperVCommands().RunAsync(Load + """
                $script:machine=[pscustomobject]@{Notes='Aviary:22222222-2222-2222-2222-222222222222';State='Off';GuestControlledCacheTypes=$false;LowMemoryMappedIoSpace=123;HighMemoryMappedIoSpace=456}
                $script:settings=@();$script:removed=@()
                function Get-VM { param($Id) $script:machine }
                function Set-VM { param($VM,$GuestControlledCacheTypes,$LowMemoryMappedIoSpace,$HighMemoryMappedIoSpace)
                    $script:settings+=@{Cache=$GuestControlledCacheTypes;Low=$LowMemoryMappedIoSpace;High=$HighMemoryMappedIoSpace}
                }
                function Get-VMGpuPartitionAdapter { param($VM) @([pscustomobject]@{InstancePath='selected'},[pscustomobject]@{InstancePath='unrelated'}) }
                function Remove-VMGpuPartitionAdapter { [CmdletBinding()]param([Parameter(ValueFromPipeline)]$VMGpuPartitionAdapter) process { $script:removed+=$VMGpuPartitionAdapter.InstancePath } }
                """ + "\n" + (failAtStart
                    ? "function Add-VMGpuPartitionAdapter { param($VM,$InstancePath) }; function Start-VM {param($VM) throw 'simulated failure'}\n"
                    : "function Add-VMGpuPartitionAdapter { param($VM,$InstancePath) throw 'simulated failure' }; function Start-VM {param($VM) throw 'must not start'}\n") + """
                try { Set-GpuHardware $script:machine ([pscustomobject]@{Name='selected'}) $p.State; throw 'failure swallowed' }
                catch { if($_.Exception.Message -ne 'simulated failure'){throw} }
                if($script:settings.Count -ne 2 -or $script:settings[-1].Cache -or $script:settings[-1].Low -ne 123 -or $script:settings[-1].High -ne 456){throw 'settings not restored'}
                if($script:removed.Count -ne 1 -or $script:removed[0] -ne 'selected'){throw 'wrong GPU removed'}
                if(Test-Path -LiteralPath $p.State){throw 'stale recovery after successful rollback'}
                'restored'
                """, Arguments(state));
            Assert.EndsWith("restored", result);
        }
        finally { Directory.Delete(directory, true); }
    }
}
