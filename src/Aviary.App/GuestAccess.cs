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
        string directory = model.Store.DirectoryFor(vm.Id);
        var enabled = new ToggleSwitch { Header = vm.Engine == VmEngine.HyperV ? "Show SSH connection in Aviary" : "Local SSH forwarding", IsOn = vm.SshEnabled, OnContent = "Key-based guest connection", OffContent = vm.Engine == VmEngine.HyperV ? "Connection hidden" : "Forwarding off" };
        var host = new TextBox { Header = "Guest IP address (Hyper-V)", Text = vm.SshHost, PlaceholderText = "Shown by ipconfig inside Windows", Visibility = vm.Engine == VmEngine.HyperV ? Visibility.Visible : Visibility.Collapsed };
        var command = new TextBox { Header = "Connection command for Codex, Claude or your terminal", Text = vm.SshEnabled && (vm.Engine == VmEngine.Qemu || vm.SshHost.Length > 0) ? GuestProvisioning.SshCommand(directory) : "Prepare access and configure the guest first.", IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        var copy = Ui.Action("Copy connection command", "\uE8C8", () => { var data = new DataPackage(); data.SetText(GuestProvisioning.SshCommand(directory)); Clipboard.SetContent(data); return Task.CompletedTask; });
        copy.IsEnabled = vm.SshEnabled && (vm.Engine == VmEngine.Qemu || vm.SshHost.Length > 0);
        var error = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Error, IsClosable = false };
        var panel = Ui.Stack(enabled, host,
            Ui.Text("Preparation creates a private key and attaches an Aviary setup CD. Inside the installed guest, run setup-ssh.sh with sudo on Linux, or setup-ssh.ps1 in elevated Windows PowerShell. The script installs OpenSSH and creates aviary-agent as a standard user. Guest installation and first connection have to succeed before access is ready.", 13, true),
            Ui.Text(vm.Engine == VmEngine.Qemu ? $"The host port is 127.0.0.1:{vm.SshPort}; it is not exposed to your LAN. A restart is required to apply changes." : "Use Hyper-V Default Switch for private networking. Enter the guest's IP here; Aviary does not create host forwarding or firewall rules. Hiding this connection does not revoke the guest key; remove its authorized_keys file inside the guest to revoke access.", 12, true),
            command, copy, Ui.Text("Verify the guest host-key fingerprint on your first SSH connection. Access instructions and keys are stored in: " + GuestProvisioning.AccessDirectory(directory), 12, true), error);
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "Guest SSH access", Content = new ScrollViewer { Content = panel, MaxHeight = 480 }, PrimaryButtonText = "Save / prepare", CloseButtonText = "Close" };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                if (State(vm).Status != VmStatus.Stopped) throw new InvalidOperationException("Shut down the machine before changing SSH access.");
                if (enabled.IsOn && !vm.NetworkEnabled) throw new InvalidOperationException("This machine needs networking enabled before using SSH.");
                var updated = vm with { SshEnabled = enabled.IsOn, SshHost = host.Text.Trim(), SshPort = !vm.SshEnabled && enabled.IsOn && vm.Engine == VmEngine.Qemu ? GuestProvisioning.AvailablePort() : vm.SshPort };
                updated.Validate();
                if (enabled.IsOn && !vm.SshEnabled)
                {
                    if (vm.SetupIsoPath.Length > 0) throw new InvalidOperationException("Finish Windows setup and eject the existing setup CD before preparing another one.");
                    updated = await GuestProvisioning.PrepareAsync(updated with { LocalWindowsAccount = false }, directory);
                }
                if (enabled.IsOn) await GuestProvisioning.WriteSshConfigAsync(updated, directory);
                await SaveMachineChangeAsync(vm, updated);
            }
            catch (Exception ex) { e.Cancel = true; error.Message = ex.Message; error.IsOpen = true; }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
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
    async Task EjectSetupAsync(VmConfiguration vm)
    {
        if (State(vm).Status != VmStatus.Stopped) throw new InvalidOperationException("Shut down the machine before removing setup media.");
        await model.Backend!.EjectSetupMediaAsync(vm);
        await SaveMachineChangeAsync(vm, vm with { SetupIsoPath = "" });
        string owned = Path.GetFullPath(Path.Combine(GuestProvisioning.AccessDirectory(model.Store.DirectoryFor(vm.Id)), "setup.iso"));
        if (vm.SetupIsoPath.Length > 0 && Path.GetFullPath(vm.SetupIsoPath).Equals(owned, StringComparison.OrdinalIgnoreCase) && File.Exists(owned)) File.Delete(owned);
    }
}
