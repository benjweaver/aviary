using System.Diagnostics;
using System.Runtime.InteropServices;
using Aviary.Core;
namespace Aviary.Infrastructure;

public static class HostProbe
{
    [DllImport("kernel32.dll")] private static extern bool GetPhysicallyInstalledSystemMemory(out ulong memory);
    [DllImport("WinHvPlatform.dll")] private static extern int WHvGetCapability(int code, out int capability, uint size, out uint written);
    public static HostCapabilities Detect()
    {
        long memory = 0; bool whpx = false;
        if (OperatingSystem.IsWindows())
        {
            if (GetPhysicallyInstalledSystemMemory(out var kb)) memory = (long)(kb / 1024);
            try { whpx = WHvGetCapability(0, out var present, 4, out _) == 0 && present != 0; } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        return new(RuntimeInformation.OSArchitecture.ToString(), Environment.ProcessorCount, memory, whpx, RuntimeInformation.OSDescription);
    }
    public static void ValidateAllocation(VmConfiguration vm, HostCapabilities host)
    {
        vm.Validate(); if (vm.CpuCores > Math.Max(1, host.LogicalCpuCount - 1)) throw new InvalidDataException("Reserve at least one CPU for Windows.");
        if (host.MemoryMB > 0 && vm.MemoryMB > host.MemoryMB * 0.75) throw new InvalidDataException("Reserve at least 25% of memory for Windows.");
    }
}
public sealed record QemuInstallation(string Directory, string Version, IReadOnlyList<GuestArchitecture> Architectures, IReadOnlyList<string>? Accelerators = null, bool VirglAvailable = false);
public static class QemuDiscovery
{
    public static string Executable(GuestArchitecture architecture) => architecture switch { GuestArchitecture.X86_64 => "qemu-system-x86_64.exe", GuestArchitecture.X86 => "qemu-system-i386.exe", GuestArchitecture.Arm64 => "qemu-system-aarch64.exe", GuestArchitecture.RiscV64 => "qemu-system-riscv64.exe", _ => throw new ArgumentOutOfRangeException(nameof(architecture)) };
    public static async Task<QemuInstallation?> FindAsync(string? configured = null)
    {
        var candidates = new[] { Path.Combine(AppContext.BaseDirectory, "runtime", "qemu"), configured, Environment.GetEnvironmentVariable("AVIARY_QEMU"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qemu") }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var dir in candidates.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            var architectures = Enum.GetValues<GuestArchitecture>().Where(a => File.Exists(Path.Combine(dir!, Executable(a)))).ToArray();
            if (architectures.Length == 0 || !File.Exists(Path.Combine(dir!, "qemu-img.exe"))) continue;
            var version = await ProcessRunner.RunAsync(Path.Combine(dir!, Executable(architectures[0])), ["--version"]);
            var accelerators = await ProcessRunner.RunAsync(Path.Combine(dir!, Executable(architectures[0])), ["-accel", "help"]);
            var devices = await ProcessRunner.RunAsync(Path.Combine(dir!, Executable(architectures[0])), ["-device", "help"]);
            // Windows QEMU builds list virtio-vga-gl, but egl-headless cannot back virgl there:
            // resource creation fails and the guest shows "Display output is not active".
            bool virgl = !OperatingSystem.IsWindows() && devices.Contains("\"virtio-vga-gl\"", StringComparison.Ordinal);
            return new(Path.GetFullPath(dir!), version.Split('\n')[0].Trim(), architectures, accelerators.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), virgl);
        }
        return null;
    }
}
public static class ProcessRunner
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token = default)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + executable);
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch { if (!process.HasExited) process.Kill(true); throw; }
        var stderr = await error; var stdout = await output;
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)}: {stderr.Trim()} (exit {process.ExitCode})"); return stdout;
    }

    public sealed record Result(int ExitCode, string Output, string Error, bool TimedOut);
    // Never throws for a non-zero exit; output is capped so a runaway command can't exhaust memory.
    public static async Task<Result> RunCapturedAsync(string executable, IEnumerable<string> arguments, TimeSpan limit, CancellationToken token = default, int maxChars = 1_000_000)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + executable);
        process.StandardInput.Close();
        static async Task<string> Read(StreamReader reader, int max)
        {
            var text = new System.Text.StringBuilder(); var buffer = new char[8192]; int read;
            while ((read = await reader.ReadAsync(buffer)) > 0) if (text.Length < max) text.Append(buffer, 0, Math.Min(read, max - text.Length));
            return text.Length >= max ? text + "\n[output truncated]" : text.ToString();
        }
        var output = Read(process.StandardOutput, maxChars); var error = Read(process.StandardError, maxChars);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(limit);
        bool timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { timedOut = true; process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return new(timedOut ? -1 : process.ExitCode, await output, await error, timedOut);
    }
}


