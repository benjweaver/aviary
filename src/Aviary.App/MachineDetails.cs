using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace Aviary.App;

public sealed partial class MainWindow
{
    void ShowDetails(VmConfiguration vm)
    {
        selected = vm.Id; previewTargets.Clear(); var state = State(vm);
        var stack = new StackPanel { Spacing = 24, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
        var back = Action("Library", "\uE72B", () => { ShowLibrary(); return Task.CompletedTask; }); back.HorizontalAlignment = HorizontalAlignment.Left; stack.Children.Add(back);
        var header = new Grid { ColumnSpacing = 18 }; header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(Ui.OsIcon(vm.OperatingSystem)); var identity = Ui.Stack(Ui.Heading(vm.Name, 28), Ui.Text($"{vm.OperatingSystem} · {vm.Architecture.ToString().Replace('_', '-')}", 13, true)); Grid.SetColumn(identity, 1); header.Children.Add(identity);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center }; commands.Children.Add(PrimaryAction(vm));
        var more = new Button { Content = Ui.Icon("\uE712"), Flyout = MachineMenu(vm), Padding = new Thickness(12) }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, "More actions for " + vm.Name); commands.Children.Add(more); Grid.SetColumn(commands, 2); header.Children.Add(commands); stack.Children.Add(header);
        if (state.Error is not null) stack.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Title = "The machine needs attention", Message = state.Error });
        var columns = new Grid { ColumnSpacing = 20, RowSpacing = 20 }; columns.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); columns.RowDefinitions.Add(new() { Height = GridLength.Auto }); columns.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var screen = Ui.Stack(Preview(vm, 330), Ui.Status(state.Status), Ui.Text(vm.Engine == VmEngine.HyperV ? "Hyper-V opens in a separate Windows guest window." : previews.ContainsKey(vm.Id) ? "Last display preview from this session" : "Your desktop will appear here after you open the display.", 12, true)); var previewCard = Ui.Card(screen, 18); columns.Children.Add(previewCard);
        var summary = Ui.Stack(Ui.Heading("Configuration", 18), DetailRow("Processor", $"{vm.CpuCores} virtual CPUs"), DetailRow("Memory", Ui.Memory(vm.MemoryMB)), DetailRow("Storage", $"{vm.DiskGB} GB · {vm.DiskFormat}"), DetailRow("Engine", vm.Engine == VmEngine.HyperV ? "Hyper-V · Generation 2" : vm.Acceleration == Acceleration.Whpx ? "QEMU · WHPX" : "QEMU · TCG"), DetailRow("Display", vm.Engine == VmEngine.HyperV ? "Windows VMConnect" : vm.AcceleratedGraphics ? "3D · VirtIO (experimental)" : vm.DynamicDisplay ? "Adaptive · VirtIO" : "Compatibility · VGA"), DetailRow("Network", vm.NetworkEnabled ? vm.Engine == VmEngine.HyperV ? vm.HyperVSwitch : vm.AcceleratedNetwork ? "VirtIO · shared connection" : "Compatibility · shared connection" : "Disconnected"));
        var summaryCard = Ui.Card(summary); Grid.SetColumn(summaryCard, 1); columns.Children.Add(summaryCard);
        columns.SizeChanged += (_, e) => { bool narrow = e.NewSize.Width < 740; columns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star); Grid.SetColumn(summaryCard, narrow ? 0 : 1); Grid.SetRow(summaryCard, narrow ? 1 : 0); };
        stack.Children.Add(columns);
        stack.Children.Add(Ui.Card(Ui.Stack(Ui.Heading("Recommended setup", 18), Ui.Text(GuestRecommendations.For(vm.OperatingSystem, vm.Engine, model.Host.WhpxAvailable, model.HyperV.Available, model.Installation?.VirglAvailable == true), 13, true))));
        stack.Children.Add(Action("SSH access", "\uE756", () => SshAccessAsync(vm)));
#if AVIARY_GPU_PARTITION
        if (vm.Engine == VmEngine.HyperV && vm.OperatingSystem == "Windows") stack.Children.Add(Action("GPU sharing (experimental)", "\uE7F4", () => GpuSetupAsync(vm)));
#endif
        if (vm.SetupIsoPath.Length > 0) stack.Children.Add(Ui.Text("Setup CD attached. Run the guest SSH setup script if enabled. After Windows installation, shut down and choose Eject and remove setup CD to remove its saved account password.", 12, true));
        if (state.Status == VmStatus.Stopped || state.Status == VmStatus.Error && vm.Engine == VmEngine.Qemu) stack.Children.Add(Action("Edit configuration", "\uE70F", () => EditAsync(vm)));
        var input = vm.Engine == VmEngine.HyperV
            ? Ui.Stack(Ui.Heading("Managed by Windows", 18), Ui.Text("Open launches Windows Virtual Machine Connection. Enhanced Session features depend on host and guest support. This machine keeps running when Aviary closes.", 14, true), Ui.Text("Moving a Hyper-V machine to another PC requires Hyper-V export/import. Copying a portable Aviary folder does not transfer its Windows registration.", 12, true))
            : Ui.Stack(Ui.Heading("Feels like another window", 18), Ui.Text("Click the guest desktop to type. Move the mouse freely to your other windows. Held keys and mouse buttons are released when you switch away.", 14, true), Ui.Text("Ctrl+Alt+G returns keyboard focus to the toolbar. Adaptive display asks a supported guest desktop to follow the window size.", 12, true)); stack.Children.Add(Ui.Card(input));
        content.Content = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    static Grid DetailRow(string label, string value)
    {
        var row = new Grid { ColumnSpacing = 16 }; row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1.2, GridUnitType.Star) }); row.Children.Add(Ui.Text(label, 13, true)); var text = Ui.Text(value, 13); Grid.SetColumn(text, 1); row.Children.Add(text); return row;
    }
    MenuFlyout MachineMenu(VmConfiguration vm)
    {
        var menu = new MenuFlyout(); var state = State(vm).Status;
        void Add(string text, Symbol icon, Func<Task> action) { var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon) }; item.Click += async (_, _) => await Guard(action); menu.Items.Add(item); }
        if (state is VmStatus.Running or VmStatus.Paused)
        {
            Add(state == VmStatus.Paused ? "Resume" : "Pause", state == VmStatus.Paused ? Symbol.Play : Symbol.Pause, () => state == VmStatus.Paused ? model.Backend!.ResumeAsync(vm.Id) : model.Backend!.PauseAsync(vm.Id));
            Add("Shut down", Symbol.Stop, () => model.Backend!.StopAsync(vm.Id));
            Add("Restart", Symbol.Refresh, async () => { if (await Confirm("Restart this machine?", "This immediately resets the guest. Unsaved work may be lost.", "Restart")) await model.Backend!.ResetAsync(vm.Id); });
        }
        if (state is VmStatus.Running or VmStatus.Paused or VmStatus.Stopping) Add("Force power off…", Symbol.Stop, async () => { if (await Confirm("Force power off?", "The guest will stop immediately. Unsaved work may be lost.", "Power off")) await model.Backend!.ForceStopAsync(vm.Id); });
        if (state == VmStatus.Stopped || state == VmStatus.Error && vm.Engine == VmEngine.Qemu) Add("Edit configuration", Symbol.Edit, () => EditAsync(vm));
        Add("SSH access...", Symbol.Document, () => SshAccessAsync(vm));
        if (vm.SetupIsoPath.Length > 0 && state == VmStatus.Stopped) Add("Eject and remove setup CD", Symbol.Remove, () => EjectSetupAsync(vm));
        if (vm.Engine == VmEngine.Qemu && vm.OperatingSystem == "Windows" && state == VmStatus.Stopped)
        {
            if (vm.DriverIsoPath.Length > 0) Add("Eject VirtIO driver CD", Symbol.Remove, () => EjectDriversAsync(vm));
            else Add("Attach VirtIO driver CD…", Symbol.Add, () => AttachDriversAsync(vm));
        }
        menu.Items.Add(new MenuFlyoutSeparator()); Add("Diagnostics", Symbol.Document, () => Diagnostics(vm));
        if (state is VmStatus.Stopped or VmStatus.Error) Add("Remove from library…", Symbol.Remove, async () => { if (await Confirm("Remove this machine?", "Its virtual disk will stay on your PC. This only removes the library entry.", "Remove")) { model.Store.RemoveFromLibrary(vm.Id); model.Machines.Remove(vm); previews.Remove(vm.Id); ShowLibrary(); } });
        return menu;
    }
    async Task<string?> PickIso()
    {
        var picker = new FileOpenPicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); picker.FileTypeFilter.Add(".iso"); return (await picker.PickSingleFileAsync())?.Path;
    }
    async Task EditAsync(VmConfiguration vm)
    {
        var name = new TextBox { Header = "Machine name", Text = vm.Name, MaxLength = 100 };
        var iso = new TextBox { Header = "Installer image", Text = vm.IsoPath, PlaceholderText = "No installer attached" };
        var acceleration = new ToggleSwitch { Header = "Hardware acceleration", OnContent = "WHPX", OffContent = "Software emulation", IsOn = vm.Acceleration == Acceleration.Whpx, IsEnabled = model.Host.WhpxAvailable || vm.Acceleration == Acceleration.Whpx };
        var dynamic = new ToggleSwitch { Header = "Adaptive display", OnContent = "VirtIO display · resize with window", OffContent = "Compatibility display", IsOn = vm.DynamicDisplay };
        var fastNetwork = new ToggleSwitch { Header = "VirtIO network", IsOn = vm.AcceleratedNetwork, OnContent = "Efficient virtual adapter", OffContent = "Compatibility adapter" };
        var graphics = new ToggleSwitch { Header = model.Installation?.VirglAvailable == true ? "3D graphics (experimental)" : "3D graphics (not available with this QEMU; uses 2D display)", IsOn = vm.AcceleratedGraphics, IsEnabled = vm.OperatingSystem == "Linux" && (vm.AcceleratedGraphics || model.Installation?.VirglAvailable == true), OnContent = "Host OpenGL · VirtIO", OffContent = "Software rendering" };
        graphics.Toggled += (_, _) => { if (graphics.IsOn) dynamic.IsOn = true; };
        dynamic.Toggled += (_, _) => { if (!dynamic.IsOn) graphics.IsOn = false; };
        var error = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
        var panel = vm.Engine == VmEngine.HyperV
            ? Ui.Stack(name, iso, Ui.Text("Hyper-V applies the name and installer on the next start. Use Hyper-V Manager for hardware or Enhanced Session settings. The engine of an existing machine cannot be changed here.", 12, true), error)
            : Ui.Stack(name, iso, acceleration, dynamic, graphics, fastNetwork, Ui.Text("VirtIO networking needs a guest driver: modern Linux includes it; for Windows, attach the VirtIO driver CD from the machine menu and install the guest tools first. 3D graphics needs a QEMU build with working virgl, which Windows hosts lack; machines with it on use the 2D adaptive display. Shared networking still uses NAT.", 12, true), Ui.Text("Adaptive display changes the virtual graphics adapter. Linux needs its virtio GPU driver; Windows needs a compatible driver installed. Changes take effect on the next start.", 12, true), error);
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "Edit configuration", Content = new ScrollViewer { Content = panel, MaxHeight = 480 }, PrimaryButtonText = "Save changes", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                if (State(vm).Status is not (VmStatus.Stopped or VmStatus.Error)) throw new InvalidOperationException("Shut down the machine before editing its configuration.");
                var updated = vm with { Name = name.Text.Trim(), IsoPath = iso.Text.Trim(), Acceleration = vm.Engine == VmEngine.HyperV ? vm.Acceleration : acceleration.IsOn ? Acceleration.Whpx : Acceleration.Tcg, DynamicDisplay = vm.Engine == VmEngine.Qemu && dynamic.IsOn, AcceleratedGraphics = vm.Engine == VmEngine.Qemu && graphics.IsOn, AcceleratedNetwork = vm.Engine == VmEngine.Qemu && fastNetwork.IsOn };
                updated.Validate();
                if (updated.IsoPath.Length > 0 && !File.Exists(updated.IsoPath)) throw new InvalidDataException("Choose an existing ISO or clear the installer field.");
                if (updated.DynamicDisplay != vm.DynamicDisplay || updated.AcceleratedGraphics != vm.AcceleratedGraphics || updated.AcceleratedNetwork != vm.AcceleratedNetwork)
                {
                    var config = Path.Combine(model.Store.DirectoryFor(vm.Id), "config.json");
                    File.Copy(config, config + ".before-display-change-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".bak");
                }
                await model.Store.SaveAsync(updated); model.Machines[model.Machines.IndexOf(vm)] = updated;
                if (selected == vm.Id) ShowDetails(updated); else PopulateLibrary();
            }
            catch (Exception ex) { error.Message = ex.Message; error.IsOpen = true; e.Cancel = true; }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
    async Task Diagnostics(VmConfiguration vm)
    {
        string text = "";
        if (vm.Engine == VmEngine.HyperV) text = $"Hyper-V ID: {vm.HyperVId}\n{model.HyperV.Message}\nState: {State(vm).Status}\n{State(vm).Error}\nDisk: {vm.DiskPath}\nSwitch: {vm.HyperVSwitch}";
        else if (model.Installation is not null) try { text = QemuCommandBuilder.Preview(QemuCommandBuilder.Build(vm, model.Installation, model.Host, QemuEndpoints.Create())); } catch (Exception ex) { text = ex.Message; }
        var log = Path.Combine(model.Store.DirectoryFor(vm.Id), "logs", "stderr.jsonl"); if (File.Exists(log)) text += "\n\nRecent messages\n" + string.Join("\n", File.ReadLines(log).TakeLast(40));
        await new ContentDialog { XamlRoot = root.XamlRoot, Title = "Diagnostics", Content = new ScrollViewer { MaxHeight = 450, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Mono") } }, CloseButtonText = "Done" }.ShowAsync();
    }
    void ShowSettings()
    {
        selected = null; libraryGrid = null;
        var path = new TextBox { Header = "QEMU installation folder", Text = model.Settings.QemuPath, PlaceholderText = model.Installation?.Directory ?? @"C:\Program Files\qemu" };
        var storage = new TextBox { Header = "Machine library folder", Text = model.Settings.StoragePath };
        var stack = new StackPanel { Spacing = 24, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(Ui.Heading("Settings", 32)); stack.Children.Add(Ui.Text("Make Aviary at home on your PC.", 15, true));
        var engine = Ui.Stack(Ui.Heading("Virtualization engine", 20), Ui.Text(model.Installation?.Version ?? "QEMU is not connected", 13, true), path);
        stack.Children.Add(Ui.Card(engine));
        stack.Children.Add(Ui.Card(Ui.Stack(Ui.Heading("Hyper-V", 20), Ui.Text(model.HyperV.Message, 14, true), Ui.Text("Enable Hyper-V Platform and Management Tools in Windows Features on a supported Windows edition. Your account needs Hyper-V management permission. Aviary does not enable features or change host networking automatically.", 12, true), Ui.Text("Native Hyper-V guests open in VMConnect and stay running after Aviary closes. QEMU's WHPX acceleration is a separate option.", 12, true))));
        stack.Children.Add(Ui.Card(Ui.Stack(Ui.Heading("Library", 20), storage, Ui.Text("Changing the folder opens a different library. Existing machines and disks are not moved.", 12, true))));
        stack.Children.Add(Ui.Card(Ui.Stack(Ui.Heading("Appearance & input", 20), Ui.Text("Aviary follows your Windows theme. Keyboard input follows guest focus, and the mouse moves freely between windows.", 14, true), Ui.Text("Automatic application updates are not available yet.", 12, true))));
        stack.Children.Add(Action("Save settings", "\uE74E", async () => { if (ActiveMachines()) throw new InvalidOperationException("Shut down running machines before changing engine or library settings."); if (!Path.IsPathFullyQualified(storage.Text.Trim())) throw new InvalidDataException("Choose an absolute library folder path."); await model.SaveSettingsAsync(new() { QemuPath = path.Text.Trim(), StoragePath = storage.Text.Trim() }); await LoadAsync(); notice.Title = "Settings saved"; notice.Message = "Your engine and library preferences are up to date."; notice.Severity = InfoBarSeverity.Success; notice.IsOpen = true; }, true));
        content.Content = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}

