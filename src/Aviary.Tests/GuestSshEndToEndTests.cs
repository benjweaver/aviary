using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Xunit;
using Xunit.Abstractions;
namespace Aviary.Tests;

// Real guest check: set AVIARY_LINUX_GUEST_DISK to an installed Linux desktop qcow2 that logs in automatically and
// opens a terminal on Ctrl+Alt+T, with OpenSSH server installed. The disk is never written: the VM runs on a
// throwaway overlay. Aviary types the setup command, then runs commands in the guest over SSH.
public sealed class LinuxGuestFactAttribute : FactAttribute
{
    public LinuxGuestFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AVIARY_QEMU")) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AVIARY_LINUX_GUEST_DISK")))
            Skip = "Set AVIARY_QEMU and AVIARY_LINUX_GUEST_DISK to run the real-guest SSH test.";
    }
}

public sealed class GuestSshEndToEndTests(ITestOutputHelper output)
{
    // Alpine's live ISO logs in as root with no password and has no systemd, so this covers the nohup/autostart
    // path. Needs internet access in the guest to install openssh, bash and curl.
    [QemuFact]
    public async Task AviarySetsUpSshInAlpineLive()
    {
        var iso = Environment.GetEnvironmentVariable("AVIARY_TEST_ISO");
        if (string.IsNullOrEmpty(iso)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var root = Path.Combine(Path.GetTempPath(), "aviary-alpine-" + Guid.NewGuid());
        var installation = await QemuDiscovery.FindAsync(Environment.GetEnvironmentVariable("AVIARY_QEMU")); Assert.NotNull(installation);
        var host = HostProbe.Detect();
        var store = new VmStore(root);
        await using var backend = new QemuBackend(store, installation, host);
        try
        {
            var vm = await backend.CreateAsync(new()
            {
                Name = "alpine-ssh", OperatingSystem = "Linux", CpuCores = 2, MemoryMB = 1024, DiskGB = 1, IsoPath = iso, AcceleratedNetwork = true,
                Acceleration = host.WhpxAvailable ? Acceleration.Whpx : Acceleration.Tcg,
                SshEnabled = true, SshPort = GuestProvisioning.AvailablePort(), SshAgentPort = GuestProvisioning.AvailablePort(),
            }, timeout.Token);
            var dir = store.DirectoryFor(vm.Id);
            await backend.StartAsync(vm, timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(host.WhpxAvailable ? 30 : 90), timeout.Token);
            await backend.TypeTextAsync(vm.Id, "root\n", timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
            // The live ISO only knows its own CD repository; bash comes from an online mirror. curl is left out on purpose: setup falls back to wget.
            await backend.TypeTextAsync(vm.Id, "setup-interfaces -a && rc-service networking restart; setup-apkrepos -1 && apk add openssh bash\n", timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(45), timeout.Token);

            var setup = await backend.BeginSshSetupAsync(vm, timeout.Token);
            await backend.TypeTextAsync(vm.Id, setup.Command + "\n", timeout.Token);
            string user;
            try { user = await setup.User.WaitAsync(TimeSpan.FromMinutes(2), timeout.Token); }
            catch
            {
                var shot = Path.Combine(Path.GetTempPath(), "aviary-alpine-failed.png");
                File.WriteAllBytes(shot, await backend.ScreenshotAsync(vm.Id, CancellationToken.None)); output.WriteLine("Screen: " + shot); throw;
            }
            Assert.Equal("root", user);
            vm = vm with { SshUser = user };
            await GuestSsh.WriteConfigAsync(vm, dir, "127.0.0.1", vm.SshPort);
            Assert.Equal("root", (await GuestSsh.RunAsync(dir, "id -un", timeout.Token)).Trim());
            output.WriteLine(await GuestSsh.RunAsync(dir, "cat /etc/alpine-release; ls ~/.aviary-ssh", timeout.Token));
            var both = await Task.WhenAll(GuestSsh.RunAsync(dir, "echo one; sleep 2", timeout.Token), GuestSsh.RunAsync(dir, "echo two; sleep 2", timeout.Token));
            Assert.Equal(["one", "two"], both.Select(s => s.Trim()));
        }
        finally
        {
            await backend.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [LinuxGuestFact]
    public async Task AviarySetsUpSshInARealLinuxGuestWithoutSudo()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var root = Path.Combine(Path.GetTempPath(), "aviary-e2e-" + Guid.NewGuid());
        var installation = await QemuDiscovery.FindAsync(Environment.GetEnvironmentVariable("AVIARY_QEMU")); Assert.NotNull(installation);
        var host = HostProbe.Detect();
        var store = new VmStore(root);
        await using var backend = new QemuBackend(store, installation, host);
        try
        {
            var vm = new VmConfiguration
            {
                Name = "ssh-e2e", OperatingSystem = "Linux", CpuCores = Math.Min(4, host.LogicalCpuCount - 1), MemoryMB = 4096, DynamicDisplay = true,
                Acceleration = host.WhpxAvailable ? Acceleration.Whpx : Acceleration.Tcg, AcceleratedNetwork = true,
                SshEnabled = true, SshPort = GuestProvisioning.AvailablePort(), SshAgentPort = GuestProvisioning.AvailablePort(),
            };
            var dir = store.DirectoryFor(vm.Id); Directory.CreateDirectory(Path.Combine(dir, "disks"));
            var overlay = Path.Combine(dir, "disks", "overlay.qcow2");
            await ProcessRunner.RunAsync(Path.Combine(installation.Directory, "qemu-img.exe"), ["create", "-q", "-f", "qcow2", "-b", Environment.GetEnvironmentVariable("AVIARY_LINUX_GUEST_DISK")!, "-F", "qcow2", overlay], timeout.Token);
            vm = vm with { DiskPath = overlay };
            await store.SaveAsync(vm, timeout.Token);

            await backend.StartAsync(vm, timeout.Token);
            output.WriteLine("Booting to the desktop…");
            // Long enough to reach the desktop, short enough to beat an idle screen lock.
            var boot = int.TryParse(Environment.GetEnvironmentVariable("AVIARY_LINUX_GUEST_BOOT_SECONDS"), out var seconds) ? seconds : 55;
            await Task.Delay(TimeSpan.FromSeconds(boot), timeout.Token);
            await backend.SendChordAsync(vm.Id, QemuKeyboard.Chord("ctrl+alt+t"), timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token);

            var setup = await backend.BeginSshSetupAsync(vm, timeout.Token);
            output.WriteLine("Typing: " + setup.Command);
            await backend.TypeTextAsync(vm.Id, setup.Command + "\n", timeout.Token);
            string Shot(string name) => Path.Combine(Path.GetTempPath(), $"aviary-e2e-{name}.png");
            await Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
            File.WriteAllBytes(Shot("typed"), await backend.ScreenshotAsync(vm.Id, timeout.Token));
            string user;
            try { user = await setup.User.WaitAsync(TimeSpan.FromMinutes(2), timeout.Token); }
            catch { File.WriteAllBytes(Shot("failed"), await backend.ScreenshotAsync(vm.Id, CancellationToken.None)); output.WriteLine("Screens: " + Shot("typed") + ", " + Shot("failed")); throw; }
            output.WriteLine("Guest reported user: " + user);

            vm = vm with { SshUser = user };
            await GuestSsh.WriteConfigAsync(vm, dir, "127.0.0.1", vm.SshPort);
            Assert.Equal(user, (await GuestSsh.RunAsync(dir, "id -un", timeout.Token)).Trim());
            output.WriteLine(await GuestSsh.RunAsync(dir, "uname -a; cat /etc/os-release | head -2; systemctl --user is-active aviary-ssh", timeout.Token));
            // Two sessions at once: each needs its own idle agent connection.
            var both = await Task.WhenAll(GuestSsh.RunAsync(dir, "echo one; sleep 2", timeout.Token), GuestSsh.RunAsync(dir, "echo two; sleep 2", timeout.Token));
            Assert.Equal(["one", "two"], both.Select(s => s.Trim()));

            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "aviary-e2e-screen.png"), await backend.ScreenshotAsync(vm.Id, timeout.Token));
        }
        finally
        {
            await backend.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
