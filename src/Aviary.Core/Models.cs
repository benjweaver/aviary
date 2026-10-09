namespace Aviary.Core;

public static class Branding { public const string Name = "Aviary"; }
public enum GuestArchitecture { X86_64, X86, Arm64, RiscV64 }
public enum Acceleration { Whpx, Tcg }
public enum VmEngine { Qemu, HyperV }
public enum VmStatus { Stopped, Starting, Running, Paused, Stopping, Error }
public enum DiskFormat { Qcow2, Raw, Vhdx }
public sealed record VmConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Linux";
    public string OperatingSystem { get; init; } = "Linux";
    public GuestArchitecture Architecture { get; init; } = GuestArchitecture.X86_64;
    public VmEngine Engine { get; init; }
    public Guid? HyperVId { get; init; }
    public string HyperVSwitch { get; init; } = "";
    public Acceleration Acceleration { get; init; } = Acceleration.Tcg;
    public int CpuCores { get; init; } = 2;
    public int MemoryMB { get; init; } = 2048;
    public int DiskGB { get; init; } = 32;
    public DiskFormat DiskFormat { get; init; }
    public string IsoPath { get; init; } = "";
    public string DiskPath { get; init; } = "";
    public bool NetworkEnabled { get; init; } = true;
    // False preserves the display hardware of existing schema-1 machines.
    public bool DynamicDisplay { get; init; }
    public bool AcceleratedNetwork { get; init; }
    public bool AcceleratedGraphics { get; init; }
    public bool LocalWindowsAccount { get; init; }
    public string WindowsUserName { get; init; } = "aviary";
    // Set at creation: the answer file signs in to the local account automatically.
    public bool WindowsAutoSignIn { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string SetupPassword { get; init; } = "";
    public string SetupIsoPath { get; init; } = "";
    // Shared virtio-win driver CD for Windows guests on QEMU (NetKVM and friends).
    public string DriverIsoPath { get; init; } = "";
    public bool SshEnabled { get; init; }
    public int SshPort { get; init; } = 2222;
    public string SshHost { get; init; } = "";
    // Linux guests on QEMU dial out to Aviary on this loopback port; the guest account is reported back by setup.
    public int SshAgentPort { get; init; }
    public string SshUser { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesSshAgent => Engine == VmEngine.Qemu && OperatingSystem == "Linux";
    public void Validate()
    {
        if (AcceleratedGraphics && (Engine != VmEngine.Qemu || OperatingSystem != "Linux" || !DynamicDisplay)) throw new InvalidDataException("3D graphics requires a QEMU Linux guest with adaptive display enabled.");
        if (LocalWindowsAccount && OperatingSystem != "Windows") throw new InvalidDataException("Local Windows setup requires a Windows guest.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(WindowsUserName, "^[a-zA-Z][a-zA-Z0-9_-]{0,19}$")) throw new InvalidDataException("Use a short local username containing letters, digits, underscores or hyphens.");
        if (SshEnabled && !NetworkEnabled) throw new InvalidDataException("SSH access requires networking.");
        // QEMU guests are reached through a host port; Hyper-V guests directly on port 22 at their own address.
        if (SshEnabled && (Engine == VmEngine.Qemu ? SshPort < 1024 || SshPort > 65535 : SshPort != 22)) throw new InvalidDataException("SSH access needs a host port from 1024 to 65535 for QEMU, or port 22 for Hyper-V.");
        if (SshHost.Length > 0 && !System.Net.IPAddress.TryParse(SshHost, out _)) throw new InvalidDataException("Enter the guest IP address for SSH.");
        if (SshAgentPort != 0 && (SshAgentPort < 1024 || SshAgentPort > 65535 || SshAgentPort == SshPort)) throw new InvalidDataException("The SSH agent port must be a separate host port from 1024 to 65535.");
        if (SshUser.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(SshUser, "^[A-Za-z0-9_][A-Za-z0-9_. -]{0,63}$")) throw new InvalidDataException("The guest reported an invalid user name.");
        if (SchemaVersion != 1) throw new InvalidDataException("Unsupported configuration version.");
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 100) throw new InvalidDataException("Enter a name of 1–100 characters.");
        if (!Enum.IsDefined(Architecture) || !Enum.IsDefined(Acceleration) || !Enum.IsDefined(DiskFormat) || !Enum.IsDefined(Engine)) throw new InvalidDataException("Unknown configuration value.");
        if (Engine == VmEngine.HyperV && (Architecture != GuestArchitecture.X86_64 || DiskFormat != DiskFormat.Vhdx)) throw new InvalidDataException("Hyper-V machines require x86-64 and VHDX storage.");
        if (Engine == VmEngine.Qemu && DiskFormat == DiskFormat.Vhdx) throw new InvalidDataException("Choose QCOW2 or RAW for QEMU machines.");
        if (CpuCores < 1 || CpuCores > 256 || MemoryMB < 256 || MemoryMB > 1048576 || DiskGB < 1 || DiskGB > 65536) throw new InvalidDataException("Invalid hardware allocation.");
    }
}
public sealed record HostCapabilities(string Architecture, int LogicalCpuCount, long MemoryMB, bool WhpxAvailable, string WindowsVersion);
public sealed record BackendCapabilities(bool HardwareAcceleration, IReadOnlyList<GuestArchitecture> Architectures);
public sealed record VmState(Guid Id, VmStatus Status, string? Error = null, int? ProcessId = null, DateTimeOffset? StartedAt = null);
public interface IVirtualMachineBackend
{
    Task<VmConfiguration> CreateAsync(VmConfiguration configuration, CancellationToken token = default);
    Task StartAsync(VmConfiguration configuration, CancellationToken token = default);
    Task StopAsync(Guid id, CancellationToken token = default);
    Task ForceStopAsync(Guid id, CancellationToken token = default);
    Task PauseAsync(Guid id, CancellationToken token = default);
    Task ResumeAsync(Guid id, CancellationToken token = default);
    Task ResetAsync(Guid id, CancellationToken token = default);
    VmState GetStatus(Guid id);
    event Action<VmState>? StateChanged;
}
// Opens a connection to a running guest's display (RFB). Local-only: Aviary never exposes displays on TCP.
public interface IDisplayConnection { Task<Stream> OpenAsync(CancellationToken token); }
