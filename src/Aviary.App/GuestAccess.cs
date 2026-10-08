using Aviary.Core;
using Aviary.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Aviary.App;

public sealed partial class MainWindow
{
    async Task SaveMachineChangeAsync(VmConfiguration original, VmConfiguration updated)
    {
        await model.Store.SaveAsync(updated);
        model.Machines[model.Machines.IndexOf(original)] = updated;
        ShowDetails(updated);
    }
    async Task SshAccessAsync(VmConfiguration vm)
    {
        var backend = model.Backend ?? throw new InvalidOperationException("Configure an engine in Settings first.");
        string directory = model.Store.DirectoryFor(vm.Id);
        var current = vm;
        var enabled = new ToggleSwitch { Header = "SSH access", IsOn = vm.SshEnabled, OnContent = "On", OffContent = "Off" };
        var status = Ui.Text("", 13, true);
        var instruction = Ui.Text(GuestSsh.SetupInstruction(vm), 13, true);
        var setupCommand = new TextBox { Header = "Setup command (run once in the guest)", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        var connect = new TextBox { Header = "Connect from Claude, Codex or your terminal", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Text = GuestProvisioning.SshCommand(directory), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        var error = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Error, IsClosable = true };
        static void Copy(string text) { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); }
        Button? setupButton = null, typeButton = null, copySetup = null;
        void Refresh()
        {
            bool running = State(current).Status == VmStatus.Running;
            status.Text = !current.SshEnabled ? "Off. Turn on to let tools connect to this machine over SSH."
                : current.SshUser.Length > 0 ? $"Set up for {current.SshUser}. Run setup again after reinstalling the guest."
                : running ? "On. Run setup in the guest to finish." : "On. Start the machine, sign in, then run setup.";
            setupButton!.IsEnabled = current.SshEnabled && running;
            connect.Visibility = current.SshEnabled && current.SshUser.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            instruction.Visibility = setupCommand.Visibility = typeButton!.Visibility = copySetup!.Visibility = setupCommand.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        async Task Save(VmConfiguration updated) { updated.Validate(); await model.Store.SaveAsync(updated); model.Machines[model.Machines.IndexOf(current)] = updated; current = updated; }
        async Task Guarded(Func<Task> action) { try { error.IsOpen = false; await action(); } catch (Exception ex) { error.Message = ex.Message; error.IsOpen = true; } Refresh(); }

        enabled.Toggled += async (_, _) => await Guarded(async () =>
        {
            if (enabled.IsOn == current.SshEnabled) return;
            if (!enabled.IsOn) { await Save(current with { SshEnabled = false }); setupCommand.Text = ""; return; }
            if (!current.NetworkEnabled) { enabled.IsOn = false; throw new InvalidOperationException("Turn on networking for this machine first."); }
            int sshPort = current.Engine == VmEngine.Qemu ? GuestProvisioning.AvailablePort() : 22, agentPort = 0;
            if (current.Engine == VmEngine.Qemu) do agentPort = GuestProvisioning.AvailablePort(); while (agentPort == sshPort);
            var updated = current with { SshEnabled = true, SshPort = sshPort, SshAgentPort = agentPort };
            await GuestSsh.EnsureKeysAsync(updated, directory);
            await Save(updated);
            backend.EnableSsh(updated);
            if (State(updated).Status == VmStatus.Running && updated.Engine == VmEngine.Qemu && !updated.UsesSshAgent)
                status.Text = "Restart the machine to open its SSH port, then run setup.";
        });
        setupButton = Ui.Action("Set up in guest", "\uE756", () => Guarded(async () =>
        {
            var setup = await backend.BeginSshSetupAsync(current);
            setupCommand.Text = setup.Command; Refresh();
            status.Text = "Waiting for the guest to run the setup command…";
            var user = await setup.User;
            var updated = current with { SshUser = user };
            await Save(updated);
            var (host, port) = await backend.SshEndpointAsync(updated);
            await GuestSsh.WriteConfigAsync(updated, directory, host, port);
            var who = (await GuestSsh.RunAsync(directory, updated.OperatingSystem == "Windows" ? "$env:USERNAME" : "id -un")).Trim();
            setupCommand.Text = "";
            Refresh(); status.Text = $"Ready. Connected as {who}.";
        }));
        typeButton = Ui.Action("Type it into the guest", "\uE765", () => Guarded(async () =>
        {
            await backend.TypeTextAsync(current.Id, setupCommand.Text);
            await backend.PressKeysAsync(current.Id, "enter");
        }));
        copySetup = Ui.Action("Copy", "\uE8C8", () => { Copy(setupCommand.Text); return Task.CompletedTask; });
        var copyConnect = Ui.Action("Copy connection command", "\uE8C8", () => { Copy(connect.Text); return Task.CompletedTask; });
        Refresh();

        var panel = Ui.Stack(enabled, status, setupButton, instruction, setupCommand, typeButton, copySetup, connect, copyConnect,
            Ui.Text(vm.UsesSshAgent
                ? "Linux guests connect out to Aviary, so guest firewalls need no changes and no sudo is used. Sessions run as the user who ran setup. Only this PC can connect, and the guest's host key is pinned by Aviary."
                : vm.Engine == VmEngine.HyperV
                    ? "Windows guests on Hyper-V get OpenSSH Server, reachable only from this PC. Aviary connects to the guest's address on its virtual switch and pins its host key."
                    : "Windows guests get OpenSSH Server, reachable only through 127.0.0.1 on this PC. The guest's host key is pinned by Aviary.", 12, true),
            Ui.Text("Keys and connection settings: " + GuestProvisioning.AccessDirectory(directory), 12, true), error);
        await new ContentDialog { XamlRoot = root.XamlRoot, Title = "SSH access · " + vm.Name, Content = new ScrollViewer { Content = panel, MaxHeight = 560 }, CloseButtonText = "Close" }.ShowAsync();
        ShowDetails(current);
    }
#if AVIARY_GPU_PARTITION
    // Experimental Hyper-V GPU-P; excluded from release builds until validated in a real guest.
    async Task GpuSetupAsync(VmConfiguration vm)
    {
        if (vm.HyperVId is null) throw new InvalidOperationException("This machine is not registered with Hyper-V.");
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot, Title = "Hyper-V GPU sharing (experimental)",
            Content = Ui.Stack(
                Ui.Text("Use a partition of your host GPU in an installed Windows guest. Requires a GPU exposed by Windows for partitioning, matching host and guest Windows builds, and a guest administrator password. Consumer GPUs on desktop Windows are experimental and are not a Microsoft-supported GPU-P configuration.", 14, true),
                Ui.Text("Start the guest and save your work first. Setup asks for administrator access, copies the matching host graphics driver into the guest, shuts it down normally, adds a GPU partition, and restarts it. Your guest password is not saved.", 13, true),
                Ui.Text("After setup, check the GPU in Device Manager and dxdiag, then test a 3D app. VMConnect stays available; smooth GPU-backed desktop streaming may need separate guest software. Aviary does not report graphics as verified just because a partition was attached.", 13, true),
                Ui.Text("Undo removes Aviary's GPU assignment and restores the previous VM memory settings. Copied guest driver files remain. Repeat setup after host GPU driver updates.", 12, true)),
            PrimaryButtonText = "Set up GPU…", SecondaryButtonText = "Undo GPU setup…", CloseButtonText = "Close"
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;
        string script = Path.Combine(AppContext.BaseDirectory, "Tools", "setup-hyperv-gpu.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("GPU setup helper is missing.", script);
        string mode = result == ContentDialogResult.Primary ? "Setup" : "Remove";
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = true, Verb = "runas",
            Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{script}\" -VmId {vm.HyperVId.Value:D} -AviaryId {vm.Id:D} -Mode {mode}"
        };
        // A visible console is intentional: this user-invoked helper collects guest credentials.
        using var process = System.Diagnostics.Process.Start(start);
    }
#endif
    async Task AttachDriversAsync(VmConfiguration vm)
    {
        if (State(vm).Status != VmStatus.Stopped) throw new InvalidOperationException("Shut down the machine before attaching the driver CD.");
        if (!VirtioDrivers.IsCached)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1 };
            using var cancel = new CancellationTokenSource();
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "Downloading VirtIO drivers", Content = Ui.Stack(Ui.Text($"virtio-win {VirtioDrivers.Version} · {VirtioDrivers.Size / 1_000_000} MB. Downloaded once and shared by all Windows machines.", 13, true), bar), CloseButtonText = "Cancel" };
            dialog.CloseButtonClick += (_, _) => cancel.Cancel();
            var shown = dialog.ShowAsync();
            try { await VirtioDrivers.EnsureAsync(new Progress<double>(fraction => bar.Value = fraction), cancel.Token); }
            catch (OperationCanceledException) { return; }
            finally { dialog.Hide(); await shown; }
        }
        await SaveMachineChangeAsync(vm, vm with { DriverIsoPath = VirtioDrivers.CachedPath });
        await new ContentDialog { XamlRoot = root.XamlRoot, Title = "Driver CD attached", Content = Ui.Text("Start the machine, open the CD in File Explorer and run virtio-win-guest-tools.exe. Then shut down and turn on VirtIO network in Edit configuration.", 13, true), CloseButtonText = "OK" }.ShowAsync();
    }
    async Task EjectDriversAsync(VmConfiguration vm)
    {
        if (State(vm).Status != VmStatus.Stopped) throw new InvalidOperationException("Shut down the machine before ejecting the driver CD.");
        // The ISO is shared with other machines, so it stays in the Drivers folder.
        await SaveMachineChangeAsync(vm, vm with { DriverIsoPath = "" });
    }
    async Task EjectSetupAsync(VmConfiguration vm)
    {
        if (State(vm).Status != VmStatus.Stopped) throw new InvalidOperationException("Shut down the machine before removing setup media.");
        await model.Backend!.EjectSetupMediaAsync(vm);
        await SaveMachineChangeAsync(vm, vm with { SetupIsoPath = "" });
        string owned = Path.GetFullPath(Path.Combine(GuestProvisioning.AccessDirectory(model.Store.DirectoryFor(vm.Id)), "setup.iso"));
        if (vm.SetupIsoPath.Length > 0 && Path.GetFullPath(vm.SetupIsoPath).Equals(owned, StringComparison.OrdinalIgnoreCase) && File.Exists(owned)) File.Delete(owned);
    }
}
