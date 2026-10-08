using Aviary.Qemu;
namespace Aviary.Tests;

// Command-line tests don't start QEMU, so any pipe name and socket path will do.
static class TestEndpoints
{
    public static readonly QemuEndpoints Fake = new("aviary-qmp-test", @"C:\Aviary\run\test.vnc");
}
