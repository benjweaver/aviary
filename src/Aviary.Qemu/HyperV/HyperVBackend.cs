using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Aviary.Core;
using Aviary.Infrastructure;

namespace Aviary.HyperV;

public sealed record HyperVAvailability(bool Available, string Message, string[] Switches);

public sealed class HyperVBackend(VmStore store, HostCapabilities host, IHyperVCommands commands) : IVirtualMachineBackend, IAsyncDisposable
{
    readonly ConcurrentDictionary<Guid, VmConfiguration> machines = new();
    readonly ConcurrentDictionary<Guid, VmState> states = new();
    readonly SemaphoreSlim gate = new(1);
    readonly CancellationTokenSource lifetime = new();
    Task? monitor;
    int disposed;
    public event Action<VmState>? StateChanged;
    public static async Task<HyperVAvailability> ProbeAsync(IHyperVCommands commands)
    {
        try
        {
            using var json = JsonDocument.Parse(await commands.RunAsync(HyperVScripts.Probe, new { }));
            var switches = json.RootElement.GetProperty("Switches").EnumerateArray().Select(s => s.GetString()!).ToArray();
            return new(true, "Hyper-V is ready. Guest windows open in Windows Virtual Machine Connection.", switches);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or JsonException)
        { return new(false, ex.Message + " Your account also needs Hyper-V management permission.", []); }
    }
    public async Task InitializeAsync(IEnumerable<VmConfiguration> configurations)
    {
        foreach (var vm in configurations.Where(v => v.Engine == VmEngine.HyperV)) machines[vm.Id] = vm;
        await RefreshAsync(); monitor = MonitorAsync();
    }
    public VmState GetStatus(Guid id) => states.GetValueOrDefault(id) ?? new(id, VmStatus.Stopped);
    void Set(Guid id, VmStatus status, string? error = null)
    {
        var next = new VmState(id, status, error);
        if (states.TryGetValue(id, out var previous) && previous == next) return;
        states[id] = next; StateChanged?.Invoke(next);
    }
    public static VmStatus MapState(string state) => state switch
    {
        "Running" => VmStatus.Running, "Off" => VmStatus.Stopped, "Paused" or "Saved" => VmStatus.Paused,
        "Starting" or "Resuming" or "Restoring" => VmStatus.Starting,
        "Stopping" or "Saving" or "Pausing" => VmStatus.Stopping, _ => VmStatus.Error
    };
    static object Identity(VmConfiguration vm) => new { vm.Id, vm.HyperVId };
    VmConfiguration Require(Guid id) => machines.TryGetValue(id, out var vm) && vm.HyperVId is not null && vm.HyperVId != Guid.Empty ? vm : throw new InvalidOperationException("This machine has no Hyper-V registration on this PC.");
    public async Task<VmConfiguration> CreateAsync(VmConfiguration configuration, CancellationToken token = default)
    {
        HostProbe.ValidateAllocation(configuration, host);
        if (configuration.Engine != VmEngine.HyperV) throw new InvalidDataException("Choose the Hyper-V backend.");
        if (configuration.NetworkEnabled && string.IsNullOrWhiteSpace(configuration.HyperVSwitch)) throw new InvalidDataException("Choose an existing Hyper-V virtual switch, or disconnect networking.");
        if (!File.Exists(configuration.IsoPath)) throw new FileNotFoundException("Choose an existing installer ISO.");
        await gate.WaitAsync(token);
        try
        {
            var directory = store.DirectoryFor(configuration.Id);
            if (Directory.Exists(directory)) throw new IOException("This machine directory already exists.");
            Directory.CreateDirectory(Path.Combine(directory, "disks"));
            var vm = await GuestProvisioning.PrepareAsync(configuration with { DiskPath = Path.Combine(directory, "disks", "system.vhdx"), DynamicDisplay = false }, directory, token);
            using var result = JsonDocument.Parse(await commands.RunAsync(HyperVScripts.Create, new
            {
                vm.Id, Name = vm.Name, vm.CpuCores, vm.MemoryMB, vm.DiskGB, vm.DiskPath, vm.IsoPath, vm.SetupIsoPath,
                Directory = directory, vm.NetworkEnabled, Switch = vm.HyperVSwitch, Windows = vm.OperatingSystem == "Windows"
            }, token));
            vm = vm with { HyperVId = result.RootElement.GetProperty("Id").GetGuid() };
            if (vm.HyperVId == Guid.Empty) throw new IOException("Hyper-V did not return a valid machine ID.");
            try { await store.SaveAsync(vm, token); }
            catch { await commands.RunAsync(HyperVScripts.RemoveRegistration, Identity(vm), CancellationToken.None); throw; }
            machines[vm.Id] = vm; Set(vm.Id, VmStatus.Stopped); return vm;
        }
        finally { gate.Release(); }
    }
    public async Task StartAsync(VmConfiguration configuration, CancellationToken token = default)
    {
        configuration.Validate();
        if (!File.Exists(configuration.DiskPath)) throw new FileNotFoundException("The Hyper-V disk is missing. Moving native Hyper-V machines requires export/import in Hyper-V Manager.");
        if (configuration.IsoPath.Length > 0 && !File.Exists(configuration.IsoPath)) throw new FileNotFoundException("The installer ISO is missing. Clear or replace it in Edit configuration.");
        if (configuration.SetupIsoPath.Length > 0 && !File.Exists(configuration.SetupIsoPath)) throw new FileNotFoundException("The setup CD is missing. Eject setup media in this machine's menu.");
        machines[configuration.Id] = configuration; var vm = Require(configuration.Id);
        await ExecuteAsync(vm.Id, HyperVScripts.Start, new { vm.Id, vm.HyperVId, vm.Name, vm.DiskPath, vm.IsoPath, vm.SetupIsoPath, ManagedSetupIsoPath = Path.Combine(GuestProvisioning.AccessDirectory(store.DirectoryFor(vm.Id)), "setup.iso") }, token);
    }
    async Task ExecuteAsync(Guid id, string script, object arguments, CancellationToken token)
    {
        Require(id); await gate.WaitAsync(token);
        try
        {
            if (script == HyperVScripts.Start || script == HyperVScripts.Resume || script == HyperVScripts.Reset) Set(id, VmStatus.Starting);
            else Set(id, VmStatus.Stopping);
            await commands.RunAsync(script, arguments, token); await RefreshCoreAsync(token);
        }
        catch (Exception ex) { Set(id, VmStatus.Error, ex.Message); throw; }
        finally { gate.Release(); }
    }
    public Task StopAsync(Guid id, CancellationToken token = default) => ExecuteAsync(id, HyperVScripts.Shutdown, Identity(Require(id)), token);
    public Task ForceStopAsync(Guid id, CancellationToken token = default) => ExecuteAsync(id, HyperVScripts.ForceOff, Identity(Require(id)), token);
    public Task PauseAsync(Guid id, CancellationToken token = default) => ExecuteAsync(id, HyperVScripts.Pause, Identity(Require(id)), token);
    public Task ResumeAsync(Guid id, CancellationToken token = default) => ExecuteAsync(id, HyperVScripts.Resume, Identity(Require(id)), token);
    public Task ResetAsync(Guid id, CancellationToken token = default) => ExecuteAsync(id, HyperVScripts.Reset, Identity(Require(id)), token);
    public Task EjectSetupMediaAsync(VmConfiguration vm) => commands.RunAsync(HyperVScripts.Require + "\nif ($vm.State -ne 'Off') { throw 'Shut down before ejecting setup media.' }; Get-VMDvdDrive -VM $vm | Where-Object { $_.Path -eq $p.SetupIsoPath -and $_.ControllerLocation -eq 2 } | Set-VMDvdDrive -Path $null", new { vm.Id, vm.HyperVId, vm.SetupIsoPath });
    public async Task OpenConsoleAsync(Guid id)
    {
        var vm = Require(id); await commands.RunAsync(HyperVScripts.Require, Identity(vm));
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "vmconnect.exe")) { UseShellExecute = false };
        foreach (var arg in new[] { "localhost", "-G", vm.HyperVId!.Value.ToString("D") }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Windows Virtual Machine Connection could not open.");
        await Task.Run(() =>
        {
            if (!process.WaitForInputIdle(15000)) throw new IOException("VMConnect did not become ready. The machine was not automatically started; open its console and start it there.");
        });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            process.Refresh();
            if (process.HasExited) throw new IOException("VMConnect closed before its window was ready. Open the console again to start this machine.");
            if (process.MainWindowHandle != IntPtr.Zero) break;
            if (DateTime.UtcNow >= deadline) throw new IOException("VMConnect has not shown its window. Start the machine from that window once it appears.");
            await Task.Delay(100);
        }
    }
    async Task<(string[] Ips, string? Report)> GuestReportAsync(VmConfiguration vm, CancellationToken token)
    {
        using var json = JsonDocument.Parse(await commands.RunAsync(HyperVScripts.GuestReport, Identity(vm), token));
        var ips = json.RootElement.TryGetProperty("Ips", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(i => i.GetString()!).ToArray() : [];
        var report = json.RootElement.TryGetProperty("Report", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        return (ips, report);
    }

    // The guest's IPv4 address as reported by Hyper-V's data exchange service; SSH connects to it directly.
    public async Task<string> GuestAddressAsync(Guid id, CancellationToken token = default)
    {
        var (ips, _) = await GuestReportAsync(Require(id), token);
        return ips.FirstOrDefault() ?? throw new InvalidOperationException("Hyper-V hasn't reported an IP address for this guest yet. Wait for it to finish booting; it needs a network connection and integration services.");
    }

    // Windows guests only: the script is pushed in with Copy-VMFile and reports back through KVP.
    public async Task<GuestSsh.Setup> BeginSshSetupAsync(VmConfiguration configuration, CancellationToken token = default)
    {
        var vm = Require(configuration.Id);
        if (configuration.OperatingSystem != "Windows") throw new NotSupportedException("SSH setup for Hyper-V currently supports Windows guests. Use QEMU for Linux guests.");
        if (GetStatus(vm.Id).Status != VmStatus.Running) throw new InvalidOperationException("Start the machine and sign in first.");
        var keys = await GuestSsh.EnsureKeysAsync(configuration, store.DirectoryFor(vm.Id), token);
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var staged = Path.Combine(GuestProvisioning.AccessDirectory(store.DirectoryFor(vm.Id)), "ssh-setup.ps1");
        await File.WriteAllTextAsync(staged, GuestSsh.WindowsSetupScript(keys, "gateway", "kvp:" + nonce, OpenSshInstaller.GuestPath), new System.Text.UTF8Encoding(true), token);
        try
        {
            // The installer goes in over VMBus too, so OpenSSH installs without the guest's network or Windows Update.
            await commands.RunAsync(HyperVScripts.CopyIntoGuest, new { vm.Id, vm.HyperVId, Source = await OpenSshInstaller.EnsureAsync(token), Destination = OpenSshInstaller.GuestPath }, token);
            await commands.RunAsync(HyperVScripts.CopyIntoGuest, new { vm.Id, vm.HyperVId, Source = staged, Destination = GuestSsh.WindowsSetupPath }, token);
        }
        finally { File.Delete(staged); }
        return new($"powershell -NoProfile -ExecutionPolicy Bypass -File {GuestSsh.WindowsSetupPath}", WaitForReportAsync(vm, nonce));
    }

    async Task<string> WaitForReportAsync(VmConfiguration vm, string nonce)
    {
        var deadline = DateTime.UtcNow.AddMinutes(45); // OpenSSH Server comes from Windows Update, which can be slow in a VM
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token);
            var (_, report) = await GuestReportAsync(vm, lifetime.Token);
            if (report is null || !report.StartsWith(nonce + "/", StringComparison.Ordinal)) continue;
            var parts = report[(nonce.Length + 1)..].Split('/', 2);
            if (parts is ["ok", var user]) return user;
            throw new InvalidOperationException(parts.ElementAtOrDefault(1) == "not-admin" ? "Run the command in PowerShell opened as administrator." : "Guest SSH setup failed. See the guest PowerShell window for details.");
        }
        throw new TimeoutException("The guest didn't finish SSH setup within 45 minutes. If it's still installing, run setup again once it finishes.");
    }

    // Windows guests: turn automatic sign-in on or off. The password is used once and not kept by Aviary.
    public async Task ConfigureAutoSignInAsync(Guid id, string user, string password, bool enable, CancellationToken token = default)
    {
        var vm = Require(id);
        if (vm.OperatingSystem != "Windows") throw new NotSupportedException("Automatic sign-in setup is for Windows guests.");
        if (GetStatus(id).Status != VmStatus.Running) throw new InvalidOperationException("Start the machine and wait for Windows to finish booting first.");
        if (user.Length == 0 || password.Length == 0) throw new ArgumentException("Enter the guest account name and its password.");
        try { await commands.RunWithSecretAsync(HyperVScripts.AutoSignIn, new { vm.Id, vm.HyperVId, User = user, Enable = enable }, password, token); }
        catch (IOException ex) when (ex.Message.Contains("credential", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("Windows in the guest rejected that account name or password. Use the local account's password, not a PIN.", ex); }
    }

    // Copies host files (folders recursively) into the guest over VMBus; returns the guest folder they went to.
    public async Task<string> SendFilesAsync(Guid id, string guestUser, IReadOnlyList<string> paths, CancellationToken token = default)
    {
        var vm = Require(id);
        if (vm.OperatingSystem != "Windows") throw new NotSupportedException("Copying files into Hyper-V guests needs a Windows guest.");
        if (GetStatus(id).Status != VmStatus.Running) throw new InvalidOperationException("Start the machine first.");
        var target = guestUser.Length > 0 && !guestUser.Any(c => Path.GetInvalidFileNameChars().Contains(c)) ? $@"C:\Users\{guestUser}\Downloads" : @"C:\Users\Public\Downloads";
        foreach (var path in paths)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            var files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(f => (Source: f, Relative: Path.Combine(name, Path.GetRelativePath(path, f))))
                : [(path, name)];
            foreach (var (source, relative) in files)
                await commands.RunAsync(HyperVScripts.CopyIntoGuest, new { vm.Id, vm.HyperVId, Source = source, Destination = Path.Combine(target, relative) }, token);
        }
        return target;
    }

    public Task TypeTextAsync(Guid id, string text, CancellationToken token = default) => KeyboardAsync(id, HyperVKeyboard.Text(text).ToArray(), token);
    public Task PressKeysAsync(Guid id, string combo, CancellationToken token = default) => KeyboardAsync(id, [HyperVKeyboard.Chord(combo)], token);
    async Task KeyboardAsync(Guid id, HyperVKeyboard.Step[] steps, CancellationToken token)
    {
        var vm = Require(id);
        await commands.RunAsync(HyperVScripts.Keyboard, new { vm.Id, vm.HyperVId, Steps = steps.Select(s => new { s.Text, s.Keys }).ToArray() }, token);
    }

    public async Task<byte[]> ScreenshotAsync(Guid id, CancellationToken token = default)
    {
        // Hyper-V scales the guest display into this frame; larger sizes such as 1280x800 fail with 32775 (invalid parameter).
        const int width = 1024, height = 768;
        var vm = Require(id);
        var pixels = Convert.FromBase64String(await commands.RunAsync(HyperVScripts.Thumbnail, new { vm.Id, vm.HyperVId, Width = width, Height = height }, token));
        return Png.FromRgb565(pixels, width, height);
    }

    public async Task RefreshAsync()
    {
        await gate.WaitAsync(lifetime.Token);
        try { await RefreshCoreAsync(lifetime.Token); }
        catch (Exception ex) when (ex is IOException or JsonException or System.ComponentModel.Win32Exception || ex is OperationCanceledException && !lifetime.IsCancellationRequested) { foreach (var id in machines.Keys) Set(id, VmStatus.Error, ex.Message); }
        finally { gate.Release(); }
    }
    async Task RefreshCoreAsync(CancellationToken token)
    {
        if (machines.IsEmpty) return;
        var registered = machines.Values.Where(v => v.HyperVId is not null && v.HyperVId != Guid.Empty).ToArray();
        foreach (var vm in machines.Values.Where(v => v.HyperVId is null || v.HyperVId == Guid.Empty)) Set(vm.Id, VmStatus.Error, "Hyper-V registration is missing.");
        if (registered.Length == 0) return;
        using var json = JsonDocument.Parse(await commands.RunAsync(HyperVScripts.Status, new { Machines = registered.Select(Identity).ToArray() }, token));
        foreach (var row in json.RootElement.EnumerateArray())
        {
            var id = row.GetProperty("Id").GetGuid(); if (!machines.ContainsKey(id)) continue;
            string state = row.GetProperty("State").GetString()!;
            Set(id, MapState(state), row.GetProperty("Error").GetString() ?? (MapState(state) == VmStatus.Error ? "Hyper-V state: " + state : null));
        }
    }
    async Task MonitorAsync()
    {
        try { while (!lifetime.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token); await RefreshAsync(); } }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); if (monitor is not null) await monitor;
        // Native Hyper-V guests belong to Windows and continue after Aviary closes.
        lifetime.Dispose(); gate.Dispose();
    }
}
