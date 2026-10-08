using System.Diagnostics;
using System.Text.Json;
using Aviary.Core;
using Aviary.Infrastructure;
namespace Aviary.Qemu;

// Where a VM's QMP monitor and display listen: a Windows named pipe and an AF_UNIX socket. Neither is reachable from
// guests, and the socket lives in a folder only this Windows account can open (AF_UNIX checks access to the file).
public sealed record QemuEndpoints(string QmpPipe, string VncSocket)
{
    // Not under AppData: on at least one Windows 11 setup, AF_UNIX connects to sockets anywhere beneath AppData fail
    // with WSAEINVAL while the same socket elsewhere in the profile works. The profile root is private to the user.
    public static string RunDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aviary", "run");

    public static QemuEndpoints Create()
    {
        var name = Guid.NewGuid().ToString("N");
        var socket = Path.Combine(RunDirectory, name[..12] + ".vnc");
        // AF_UNIX paths are limited to 108 bytes including the terminator.
        if (System.Text.Encoding.UTF8.GetByteCount(socket) >= 108) socket = Path.Combine(Path.GetTempPath(), "aviary-" + name[..12] + ".vnc");
        if (System.Text.Encoding.UTF8.GetByteCount(socket) >= 108) throw new PathTooLongException("Your Windows profile path is too long for Aviary's display socket.");
        return new("aviary-qmp-" + name, socket);
    }
}

public static class QemuCommandBuilder
{
    public static ProcessStartInfo Build(VmConfiguration vm, QemuInstallation qemu, HostCapabilities host, QemuEndpoints endpoints)
    {
        HostProbe.ValidateAllocation(vm, host);
        var accelerator = vm.Acceleration == Acceleration.Whpx ? "whpx" : "tcg"; if (qemu.Accelerators is not null && !qemu.Accelerators.Contains(accelerator)) throw new InvalidOperationException($"This QEMU build does not provide {accelerator} acceleration.");
        if (!qemu.Architectures.Contains(vm.Architecture)) throw new InvalidOperationException("This QEMU installation does not include the selected architecture.");
        if (vm.Architecture != GuestArchitecture.X86_64 && vm.Architecture != GuestArchitecture.X86) throw new NotSupportedException("The first milestone supports x86 and x86-64 guests. ARM64 and RISC-V firmware integration is planned.");
        if (vm.Acceleration == Acceleration.Whpx && (!host.WhpxAvailable || !host.Architecture.Equals("X64", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Windows Hypervisor Platform is unavailable for this guest. Enable it in Windows Features and restart, or select software emulation.");
        // QEMU checks the current directory before its keymap data directory. The
        // app's en-US localization folder otherwise shadows the en-us keymap.
        var info = new ProcessStartInfo(Path.Combine(qemu.Directory, QemuDiscovery.Executable(vm.Architecture))) { WorkingDirectory = Path.GetFullPath(qemu.Directory), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        void Add(params string[] args) { foreach (var arg in args) info.ArgumentList.Add(arg); }
        // Machines saved with 3D on keep working where virgl is unavailable: they get the 2D adaptive display.
        bool gl = vm.AcceleratedGraphics && qemu.VirglAvailable;
        Add("-name", vm.Name, "-machine", "pc", "-accel", vm.Acceleration == Acceleration.Whpx ? "whpx" : "tcg", "-smp", vm.CpuCores.ToString(), "-m", vm.MemoryMB.ToString(), "-display", gl ? "egl-headless" : "none", "-device", "usb-tablet", "-usb");
        // Control and display stay off TCP: user networking lets every guest reach the host's loopback at 10.0.2.2,
        // so a loopback port would hand guests this VM's monitor. QEMU option values escape commas by doubling them.
        Add("-chardev", $"pipe,id=qmp,path={endpoints.QmpPipe}", "-object", "monitor-qmp,id=qmp-monitor,chardev=qmp", "-vnc", "unix:" + endpoints.VncSocket.Replace(",", ",,"));
        // JSON blockdev avoids QEMU's comma-delimited filename parsing.
        if (vm.DynamicDisplay) Add("-vga", "none", "-device", gl ? "virtio-vga-gl" : "virtio-vga");
        Add("-blockdev", JsonSerializer.Serialize(new Dictionary<string, object> { ["driver"] = vm.DiskFormat == DiskFormat.Qcow2 ? "qcow2" : "raw", ["node-name"] = "system", ["file"] = new { driver = "file", filename = Path.GetFullPath(vm.DiskPath) } }), "-device", "ide-hd,drive=system,bus=ide.0,unit=0");
        if (!string.IsNullOrWhiteSpace(vm.IsoPath)) Add("-blockdev", JsonSerializer.Serialize(new Dictionary<string, object> { ["driver"] = "raw", ["node-name"] = "installer", ["read-only"] = true, ["file"] = new { driver = "file", filename = Path.GetFullPath(vm.IsoPath) } }), "-device", "ide-cd,drive=installer,bus=ide.0,unit=1", "-boot", "order=c,once=d");
        if (vm.SetupIsoPath.Length > 0) Add("-blockdev", JsonSerializer.Serialize(new Dictionary<string, object> { ["driver"] = "raw", ["node-name"] = "setup", ["read-only"] = true, ["file"] = new { driver = "file", filename = Path.GetFullPath(vm.SetupIsoPath) } }), "-device", "ide-cd,drive=setup,bus=ide.1,unit=0");
        // The pc machine has four IDE slots: system disk, installer, setup CD and driver CD. Pinned explicitly,
        // because QEMU's automatic placement puts a third IDE device on ide.0 and fails.
        if (vm.DriverIsoPath.Length > 0) Add("-blockdev", JsonSerializer.Serialize(new Dictionary<string, object> { ["driver"] = "raw", ["node-name"] = "drivers", ["read-only"] = true, ["file"] = new { driver = "file", filename = Path.GetFullPath(vm.DriverIsoPath) } }), "-device", "ide-cd,drive=drivers,bus=ide.1,unit=1");
        string network = vm.AcceleratedNetwork ? "user,model=virtio-net-pci" : "user,model=e1000";
        // Linux guests reach SSH through Aviary's broker on SshPort instead (no inbound traffic for guest firewalls).
        if (vm.SshEnabled && !vm.UsesSshAgent) network += $",hostfwd=tcp:127.0.0.1:{vm.SshPort}-:22";
        if (vm.NetworkEnabled) Add("-nic", network); else Add("-nic", "none");
        return info;
    }
    public static string Preview(ProcessStartInfo info) => info.FileName + " " + string.Join(" ", info.ArgumentList.Select(a => "\"" + a.Replace("\"", "\\\"") + "\""));
}


