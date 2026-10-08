using Aviary.Core;
using Aviary.Infrastructure;
using Xunit;

namespace Aviary.Tests;

public sealed class PortableStoreTests
{
    [Fact]
    public async Task MovingPortableFolderPreservesManagedDiskAndIsoPaths()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "aviary-portable-" + Guid.NewGuid());
        var original = Path.Combine(fixture, "original"); var relocated = Path.Combine(fixture, "relocated");
        try
        {
            var store = new VmStore(Path.Combine(original, "Data", "Machines"), original);
            var vm = new VmConfiguration();
            vm = vm with { DiskPath = Path.Combine(store.DirectoryFor(vm.Id), "disks", "system.qcow2"), IsoPath = Path.Combine(original, "Images", "installer.iso") };
            await store.SaveAsync(vm);
            Directory.Move(original, relocated);
            var movedStore = new VmStore(Path.Combine(relocated, "Data", "Machines"), relocated);
            var loaded = await movedStore.LoadAsync();
            Assert.Empty(loaded.Errors);
            var moved = Assert.Single(loaded.Vms);
            Assert.Equal(Path.Combine(movedStore.DirectoryFor(vm.Id), "disks", "system.qcow2"), moved.DiskPath);
            Assert.Equal(Path.Combine(relocated, "Images", "installer.iso"), moved.IsoPath);
            var external = Path.Combine(Path.GetTempPath(), "external.iso");
            await movedStore.SaveAsync(moved with { IsoPath = external });
            Assert.Equal(external, Assert.Single((await movedStore.LoadAsync()).Vms).IsoPath);
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
    }
}
