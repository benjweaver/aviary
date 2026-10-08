using System.Diagnostics;
using System.Text.Json;
using Aviary.Core;
using Aviary.Infrastructure;
namespace Aviary.Qemu;

public static class QemuCommandBuilder
{
    public static ProcessStartInfo Build(VmConfiguration vm, QemuInstallation qemu, HostCapabilities host, int qmpPort, int vncPort)
    {
        HostProbe.ValidateAllocation(vm, host);
        var accelerator = vm.Acceleration == Acceleration.Whpx ? "whpx" : "tcg"; if (qemu.Accelerators is not null && !qemu.Accelerators.Contains(accelerator)) throw new InvalidOperationException($"This QEMU build does not provide {accelerator} acceleration.");
        if (!qemu.Architectures.Contains(vm.Architecture)) throw new InvalidOperationException("This QEMU installation does not include the selected architecture.");
        if (vm.Architecture != GuestArchitecture.X86_64 && vm.Architecture != GuestArchitecture.X86) throw new NotSupportedException("The first milestone supports x86 and x86-64 guests. ARM64 and RISC-V firmware integration is planned.");
        if (vm.Acceleration == Acceleration.Whpx && (!host.WhpxAvailable || !host.Architecture.Equals("X64", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Windows Hypervisor Platform is unavailable for this guest. Enable it in Windows Features and restart, or select software emulation.");
        if (qmpPort < 1024 || qmpPort > 65535 || vncPort < 5900 || vncPort > 65535) throw new ArgumentOutOfRangeException(nameof(qmpPort));
        // QEMU checks the current directory before its keymap data directory. The
        // app's en-US localization folder otherwise shadows the en-us keymap.
        var info = new ProcessStartInfo(Path.Combine(qemu.Directory, QemuDiscovery.Executable(vm.Architecture))) { WorkingDirectory = Path.GetFullPath(qemu.Directory), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        void Add(params string[] args) { foreach (var arg in args) info.ArgumentList.Add(arg); }
        Add("-name", vm.Name, "-machine", "pc", "-accel", vm.Acceleration == Acceleration.Whpx ? "whpx" : "tcg", "-smp", vm.CpuCores.ToString(), "-m", vm.MemoryMB.ToString(), "-display", vm.AcceleratedGraphics ? "egl-headless" : "none", "-vnc", $"127.0.0.1:{vncPort - 5900}", "-qmp", $"tcp:127.0.0.1:{qmpPort},server=on,wait=off", "-device", "usb-tablet", "-usb");
        // JSON blockdev avoids QEMU's comma-delimited filename parsing.
        if (vm.DynamicDisplay) Add("-vga", "none", "-device", vm.AcceleratedGraphics ? "virtio-vga-gl" : "virtio-vga");
        Add("-blockdev", JsonSerializer.Serialize(new Dictionary<string, object> { ["driver"] = vm.DiskFormat == DiskFormat.Qcow2 ? "qcow2" : "raw", ["node-name"] = "system", ["file"] = new { driver = "file", filename = Path.GetFullPath(vm.DiskPath) } }), "-device", "ide-hd,drive=system");
        if (!string.IsNullOrWhiteSpace(vm.IsoPath)) Add("-blockdev", JsonSerializer.Serialize(new Dictionary<string, object> { ["driver"] = "raw", ["node-name"] = "installer", ["read-only"] = true, ["file"] = new { driver = "file", filename = Path.GetFullPath(vm.IsoPath) } }), "-device", "ide-cd,drive=installer", "-boot", "order=c,once=d");
        if (vm.SetupIsoPath.Length > 0) Add("-blockdev", JsonSerializer.Serialize(new Dictionary<string, object> { ["driver"] = "raw", ["node-name"] = "setup", ["read-only"] = true, ["file"] = new { driver = "file", filename = Path.GetFullPath(vm.SetupIsoPath) } }), "-device", "ide-cd,drive=setup");
        string network = vm.AcceleratedNetwork ? "user,model=virtio-net-pci" : "user,model=e1000";
        if (vm.SshEnabled) network += $",hostfwd=tcp:127.0.0.1:{vm.SshPort}-:22";
        if (vm.NetworkEnabled) Add("-nic", network); else Add("-nic", "none");
        return info;
    }
    public static string Preview(ProcessStartInfo info) => info.FileName + " " + string.Join(" ", info.ArgumentList.Select(a => "\"" + a.Replace("\"", "\\\"") + "\""));
}


