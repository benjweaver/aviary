using System.Collections.ObjectModel;
using System.Text.Json;
using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Aviary.HyperV;
using CommunityToolkit.Mvvm.ComponentModel;
namespace Aviary.App;

public sealed record AppSettings
{
    public string QemuPath { get; init; } = "";
    public string StoragePath { get; init; } = Path.Combine(AppPaths.DataDirectory, "Machines");
}
public sealed class LibraryViewModel : ObservableObject
{
    public ObservableCollection<VmConfiguration> Machines { get; } = [];
    public HostCapabilities Host { get; } = HostProbe.Detect();
    public AppSettings Settings { get; private set; } = new();
    public VmStore Store { get; private set; } = null!;
    public QemuInstallation? Installation { get; private set; }
    public MachineBackend? Backend { get; private set; }
    public HyperVAvailability HyperV { get; private set; } = new(false, "Checking Hyper-V…", []);
    public string Notice { get; private set; } = "Detecting QEMU…";
    public IReadOnlyList<string> LoadErrors { get; private set; } = [];
    public static string SettingsFile => Path.Combine(AppPaths.DataDirectory, "settings.json");
    public async Task InitializeAsync()
    {
        if (File.Exists(SettingsFile)) Settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(SettingsFile)) ?? new();
        if (!Path.IsPathRooted(Settings.StoragePath)) Settings = Settings with { StoragePath = Path.GetFullPath(Settings.StoragePath, AppPaths.DataDirectory) };
        Store = new(Settings.StoragePath, AppPaths.Portable ? AppContext.BaseDirectory : null); var loaded = await Store.LoadAsync(); Machines.Clear(); foreach (var vm in loaded.Vms) Machines.Add(vm);
        Installation = await QemuDiscovery.FindAsync(Settings.QemuPath);
        LoadErrors = loaded.Errors;
        if (Backend is not null) await Backend.DisposeAsync();
        var hyperVCommands = new HyperVCommands(); HyperV = await HyperVBackend.ProbeAsync(hyperVCommands);
        HyperVBackend? native = HyperV.Available ? new(Store, Host, hyperVCommands) : null;
        if (native is not null) await native.InitializeAsync(Machines);
        Backend = new(Installation is not null ? new(Store, Installation, Host) : null, native, Machines);
        Notice = Installation is null ? "QEMU was not found. Open Settings to select its installation folder." : $"{Installation.Version} • {Host.LogicalCpuCount} host CPUs • {Host.MemoryMB / 1024} GB RAM • " + (Host.WhpxAvailable ? "WHPX available" : "WHPX unavailable — software emulation available. Enable Windows Hypervisor Platform in Windows Features for acceleration.");
        if (loaded.Errors.Count > 0) Notice += "\n" + string.Join("\n", loaded.Errors);
        OnPropertyChanged(nameof(Notice));
    }
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        var saved = settings;
        if (AppPaths.Portable && Path.GetFullPath(settings.StoragePath).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
            saved = settings with { StoragePath = Path.GetRelativePath(AppPaths.DataDirectory, settings.StoragePath) };
        var temp = SettingsFile + ".tmp"; await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(saved)); File.Move(temp, SettingsFile, true); Settings = settings;
    }
    public async Task CreateAsync(VmConfiguration vm) { if (Backend is null) throw new InvalidOperationException("Configure QEMU in Settings first."); Machines.Add(await Backend.CreateAsync(vm)); }
}
