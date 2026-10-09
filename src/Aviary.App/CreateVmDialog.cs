using Aviary.Core;
using Aviary.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Aviary.App;

public sealed class CreateVmDialog : ContentDialog
{
    readonly Window owner;
    readonly LibraryViewModel model;
    readonly StackPanel body = new() { Spacing = 18 };
    readonly StackPanel steps = new() { Spacing = 18, Margin = new Thickness(0, 4, 20, 0) };
    readonly InfoBar error = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
    readonly TextBlock heading = Ui.Heading("", 24);
    readonly TextBlock subtitle = Ui.Text("", 13, true);
    readonly ProgressBar progress = new() { Minimum = 0, Maximum = 7, Height = 3 };
    readonly TextBox name = new() { Header = "Machine name", Text = "Linux", MaxLength = 100 };
    readonly TextBox iso = new() { Header = "Installer image", PlaceholderText = "Choose an ISO file" };
    readonly ComboBox os = new() { Header = "Operating system", ItemsSource = new[] { "Linux", "Windows", "Other" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly RadioButton virtualize = new() { GroupName = "execution", Content = "Virtualize", FontSize = 18 };
    readonly RadioButton emulate = new() { GroupName = "execution", Content = "Emulate", FontSize = 18 };
    readonly RadioButton hyperV = new() { GroupName = "execution", Content = "Hyper-V", FontSize = 18 };
    readonly ComboBox nativeSwitch = new() { Header = "Hyper-V virtual switch", HorizontalAlignment = HorizontalAlignment.Stretch };
    bool Native => hyperV.IsChecked == true;
    readonly NumberBox cpu;
    readonly NumberBox ram;
    readonly NumberBox disk = new() { Header = "Capacity (GB)", Minimum = 1, Maximum = 2048, Value = 32, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    readonly ComboBox format = new() { Header = "Disk format", ItemsSource = new[] { "QCOW2 · grows as you use it", "RAW · simple disk image" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ToggleSwitch network = new() { Header = "Internet connection", IsOn = true, OnContent = "Shared with your PC", OffContent = "Disconnected" };
    readonly ToggleSwitch dynamic = new() { Header = "Adaptive display", IsOn = true, OnContent = "Resize desktop with window", OffContent = "Compatibility graphics" };
    readonly ToggleSwitch fastNetwork = new() { Header = "VirtIO network", IsOn = true, OnContent = "Efficient virtual adapter", OffContent = "Compatibility adapter" };
    readonly ToggleSwitch graphics = new() { Header = "3D graphics (experimental)", OnContent = "Host OpenGL · Linux guests", OffContent = "Software rendering" };
    readonly ToggleSwitch drivers = new() { Header = "VirtIO drivers", IsOn = true, OnContent = "Attach driver CD (NetKVM and more)", OffContent = "Don't attach" };
    readonly ToggleSwitch localAccount = new() { Header = "Local Windows account", IsOn = true, OnContent = "Skip Microsoft-account setup", OffContent = "Use standard Windows setup" };
    readonly ToggleSwitch autoSignIn = new() { Header = "Sign in automatically", IsOn = true, OnContent = "Desktop ready at startup (needed for typing and screenshot tools)", OffContent = "Show the sign-in screen" };
    readonly TextBox localUser = new() { Header = "Local username", Text = "aviary", MaxLength = 20 };
    readonly PasswordBox localPassword = new() { Header = "Local password (12+ characters)" };
    readonly ToggleSwitch ssh = new() { Header = "SSH access for tools and assistants", OnContent = "On · finish setup once the guest is installed", OffContent = "Off" };
    readonly string[] labels = ["Experience", "Operating system", "Hardware", "Storage", "Network", "Integration", "Review"];
    readonly string[] titles = ["How will you use this machine?", "Choose its operating system", "Give it room to work", "A disk of its own", "Get connected", "Make it feel at home", "Ready when you are"];
    readonly string[] descriptions = ["Choose the right balance of speed and compatibility.", "Start from an installer image on your PC.", "Leave enough resources for Windows and your other apps.", "The disk is a file on your PC, separate from your own files.", "A shared connection is the simplest place to start.", "Choose the display experience for this guest.", "Aviary will create the disk and add this machine to your library."];
    int step;
    bool creating;
    public VmConfiguration? CreatedVm { get; private set; }

    public CreateVmDialog(Window owner, LibraryViewModel model, XamlRoot xamlRoot)
    {
        this.owner = owner; this.model = model; XamlRoot = xamlRoot;
        Title = "Create a machine"; PrimaryButtonText = "Continue"; SecondaryButtonText = "Back"; CloseButtonText = "Cancel"; DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 840d;
        virtualize.IsEnabled = model.Installation is not null && model.Host.WhpxAvailable && model.Host.Architecture == "X64";
        emulate.IsEnabled = model.Installation is not null;
        hyperV.IsEnabled = model.HyperV.Available && model.Host.Architecture == "X64";
        virtualize.IsChecked = virtualize.IsEnabled; emulate.IsChecked = !virtualize.IsEnabled && emulate.IsEnabled;
        hyperV.IsChecked = !virtualize.IsEnabled && !emulate.IsEnabled && hyperV.IsEnabled;
        graphics.IsEnabled = model.Installation?.ThreeDAvailable == true;
        nativeSwitch.ItemsSource = model.HyperV.Switches; nativeSwitch.SelectedItem = model.HyperV.Switches.FirstOrDefault(s => s == "Default Switch") ?? model.HyperV.Switches.FirstOrDefault();
        cpu = new() { Header = "CPU cores", Minimum = 1, Maximum = Math.Max(1, model.Host.LogicalCpuCount - 1), Value = Math.Min(4, Math.Max(1, model.Host.LogicalCpuCount / 2)), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        ram = new() { Header = "Memory (MB)", Minimum = 256, Maximum = model.Host.MemoryMB > 0 ? Math.Floor(model.Host.MemoryMB * .75) : 4096, Value = Math.Min(4096, Math.Max(256, model.Host.MemoryMB / 4)), SmallChange = 512, LargeChange = 1024, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        double available = Math.Min(720, Math.Max(320, xamlRoot.Size.Width - 112));
        bool compact = available < 600;
        var layout = new Grid { Width = available, ColumnSpacing = compact ? 0 : 24 }; layout.ColumnDefinitions.Add(new() { Width = new GridLength(compact ? 0 : 132) }); layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        steps.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        layout.Children.Add(steps); var page = Ui.Stack(progress, heading, subtitle, body, error); Grid.SetColumn(page, 1); layout.Children.Add(page);
        Content = new ScrollViewer { Content = layout, MaxHeight = Math.Max(320, Math.Min(560, xamlRoot.Size.Height - 230)), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        os.SelectionChanged += (_, _) => { if (name.Text is "Linux" or "Windows" or "Other") name.Text = (string)os.SelectedItem; dynamic.IsOn = os.SelectedIndex == 0; fastNetwork.IsOn = os.SelectedIndex == 0; graphics.IsEnabled = os.SelectedIndex == 0 && model.Installation?.ThreeDAvailable == true; if (!graphics.IsEnabled) graphics.IsOn = false; };
        graphics.Toggled += (_, _) => { if (graphics.IsOn) dynamic.IsOn = true; };
        dynamic.Toggled += (_, _) => { if (!dynamic.IsOn) graphics.IsOn = false; };
        PrimaryButtonClick += OnContinue;
        SecondaryButtonClick += (_, e) => { e.Cancel = true; if (!creating && step > 0) { step--; ShowStep(); } };
        Closing += (_, e) => { if (creating) e.Cancel = true; };
        ShowStep();
    }
    void ShowStep()
    {
        error.IsOpen = false; body.Children.Clear(); steps.Children.Clear();
        heading.Text = titles[step]; subtitle.Text = descriptions[step]; progress.Value = step + 1; PrimaryButtonText = step == 6 ? "Create machine" : "Continue"; IsSecondaryButtonEnabled = step > 0;
        for (int i = 0; i < labels.Length; i++) { var text = Ui.Text($"{(i < step ? "✓" : (i + 1).ToString())}  {labels[i]}", 12, i != step); if (i == step) text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; steps.Children.Add(text); }
        switch (step)
        {
            case 0:
                body.Children.Add(EngineCard(virtualize, "\uE945", virtualize.IsEnabled ? "QEMU with WHPX · hardware-assisted performance." : model.Installation is null ? "Choose a QEMU installation in Settings." : "Enable Windows Hypervisor Platform for hardware acceleration."));
                body.Children.Add(EngineCard(emulate, "\uE7F4", "QEMU software emulation. Flexible, but substantially slower."));
                body.Children.Add(EngineCard(hyperV, "\uE950", hyperV.IsEnabled ? "Windows native virtualization with its own guest window." : "Requires Hyper-V and management permission. See Settings.")); break;
            case 1:
                body.Children.Add(name); body.Children.Add(os); body.Children.Add(iso);
                body.Children.Add(Ui.Action("Choose ISO…", "\uE8B7", BrowseAsync));
                body.Children.Add(Ui.Text(Native ? "Generation 2 uses UEFI. Windows guests get Secure Boot and a virtual TPM; Linux guests use Secure Boot off for compatibility." : "Windows 11 requires UEFI and TPM support. Choose Hyper-V to create a guest with those features.", 12, true)); break;
            case 2:
                body.Children.Add(Ui.Text(GuestRecommendations.For((string)os.SelectedItem, Native ? VmEngine.HyperV : VmEngine.Qemu, model.Host.WhpxAvailable, model.HyperV.Available, model.Installation?.ThreeDAvailable == true), 13, true));
                if (os.SelectedIndex == 1 && !Native && model.HyperV.Available) body.Children.Add(Ui.Action("Use recommended Hyper-V", "\uE945", () => { hyperV.IsChecked = true; ShowStep(); return Task.CompletedTask; }));
                body.Children.Add(cpu); body.Children.Add(ram); body.Children.Add(Ui.Text($"Your PC has {model.Host.LogicalCpuCount} logical CPUs and {Ui.Memory((int)model.Host.MemoryMB)} of memory.", 13, true));
                body.Children.Add(new Expander { Header = "Advanced hardware", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Ui.Text(Native ? "Architecture: x86-64\nMachine: Hyper-V Generation 2\nFirmware: UEFI" : "Architecture: x86-64\nMachine: PC\nFirmware: BIOS", 13, true) }); break;
            case 3:
                body.Children.Add(disk); if (!Native) body.Children.Add(format); body.Children.Add(Ui.Text(Native ? "VHDX · a dynamically expanding Hyper-V disk. Native Hyper-V machines are registered on this PC; moving them requires Hyper-V export/import." : "QCOW2 reserves the capacity for the guest and uses host disk space as data is written. The initial file is small.", 13, true)); break;
            case 4:
                network.OnContent = Native ? "Connected to selected switch" : "Shared with your PC";
                body.Children.Add(Ui.Card(Ui.Stack(Ui.Icon("\uE774", 28), network, Ui.Text(Native ? "Choose an existing switch. Default Switch shares Windows networking. Aviary does not create or change host switches." : "The guest can access the internet through your PC. No administrator setup is required.", 13, true)))); if (Native) body.Children.Add(nativeSwitch); else if (os.SelectedIndex == 1)
                {
                    body.Children.Add(drivers);
                    body.Children.Add(Ui.Text((VirtioDrivers.IsCached ? "" : $"Downloads virtio-win {VirtioDrivers.Version} once ({VirtioDrivers.Size / 1_000_000} MB), shared by all Windows machines. ") + "With local-account setup the drivers install automatically at first sign-in; otherwise run virtio-win-guest-tools.exe from the CD. Windows starts on the compatibility adapter; turn on VirtIO network in Edit configuration once the drivers are in.", 12, true));
                }
                else { body.Children.Add(fastNetwork); body.Children.Add(Ui.Text("Modern Linux includes the VirtIO driver. Uses shared NAT networking.", 12, true)); } break;
            case 5:
                body.Children.Add(ssh);
                body.Children.Add(Ui.Text("Lets Claude, Codex or your terminal run commands in this machine over SSH. After installing the guest, open SSH access in its menu and run the one-line setup. Linux needs no sudo; Windows asks for an administrator PowerShell. Only this PC can connect.", 12, true));
                if (os.SelectedIndex == 1) { body.Children.Add(localAccount); body.Children.Add(localUser); body.Children.Add(localPassword); body.Children.Add(autoSignIn); body.Children.Add(Ui.Text("For fresh Windows installs. Choose a password for your local account. The setup CD contains it; eject and remove setup media after installation. Disk selection and activation remain part of Windows setup.", 12, true)); }
                if (Native) { body.Children.Add(Ui.Card(Ui.Stack(Ui.Heading("Windows guest connection", 18), Ui.Text("Hyper-V opens in VMConnect. Enhanced Session can provide resizing and shared devices when supported and configured in Windows and the guest.", 14, true), Ui.Text("The embedded QEMU display and its input shortcuts do not apply to Hyper-V.", 12, true)))); break; }
                body.Children.Add(dynamic); body.Children.Add(graphics); body.Children.Add(Ui.Text("3D needs Linux Mesa and a working host OpenGL driver. Leave off for maximum compatibility.", 12, true)); body.Children.Add(Ui.Text("Adaptive display uses VirtIO graphics. Modern Linux desktops can follow your window size when their display driver is active. Windows needs a compatible guest driver.", 13, true));
                body.Children.Add(Ui.Card(Ui.Stack(Ui.Heading("Automatic input", 16), Ui.Text("Click to type. Move the pointer freely in and out. No keyboard-capture switch, and no second cursor.", 13, true)), 18));
                body.Children.Add(Ui.Text("Shared folders, clipboard, USB passthrough and audio are not available yet.", 12, true)); break;
            case 6:
                body.Children.Add(Ui.OsIcon((string)os.SelectedItem, 64)); body.Children.Add(Ui.Heading(name.Text, 22));
                body.Children.Add(Ui.Text($"{os.SelectedItem} · {(Native ? "Hyper-V" : virtualize.IsChecked == true ? "QEMU · WHPX" : "QEMU · software emulation")}\n{cpu.Value} CPUs · {Ui.Memory((int)ram.Value)} RAM\n{disk.Value} GB · {(Native ? "VHDX" : format.SelectedIndex == 0 ? "QCOW2" : "RAW")}\n{(network.IsOn ? Native ? nativeSwitch.SelectedItem : "Shared internet connection" : "Network disconnected")}\n{(Native ? "Windows Virtual Machine Connection" : dynamic.IsOn ? "Adaptive display" : "Compatibility display")}", 14));
                body.Children.Add(Ui.Text((os.SelectedIndex == 1 && localAccount.IsOn ? "Local account: " + localUser.Text.Trim() + "\n" : "") + (ssh.IsOn ? "SSH access on: finish setup from the machine menu after installing the guest." : "SSH access is off."), 12, true));
                body.Children.Add(Ui.Text("Installer: " + Path.GetFileName(iso.Text), 12, true)); body.Children.Add(Ui.Text("Library: " + model.Store.Root, 12, true)); break;
        }
    }
    async Task BrowseAsync()
    {
        try { var picker = new FileOpenPicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner)); picker.FileTypeFilter.Add(".iso"); var file = await picker.PickSingleFileAsync(); if (file is null) return; iso.Text = file.Path; if (name.Text is "Linux" or "Windows" or "Other") name.Text = Path.GetFileNameWithoutExtension(file.Name); }
        catch (Exception ex) { error.Message = ex.Message; error.IsOpen = true; }
    }
    static Border EngineCard(RadioButton choice, string glyph, string description)
    {
        var layout = new Grid { ColumnSpacing = 16 };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Ui.Icon(glyph, 24); icon.VerticalAlignment = VerticalAlignment.Center; layout.Children.Add(icon);
        var text = new StackPanel { Spacing = 6 }; text.Children.Add(choice); text.Children.Add(Ui.Text(description, 13, true)); Grid.SetColumn(text, 1); layout.Children.Add(text);
        return Ui.Card(layout, 16);
    }
    VmConfiguration Configuration() => new() { Name = name.Text.Trim(), OperatingSystem = (string)os.SelectedItem, IsoPath = iso.Text.Trim(), CpuCores = (int)cpu.Value, MemoryMB = (int)ram.Value, DiskGB = (int)disk.Value, DiskFormat = Native ? DiskFormat.Vhdx : format.SelectedIndex == 0 ? DiskFormat.Qcow2 : DiskFormat.Raw, Acceleration = virtualize.IsChecked == true ? Acceleration.Whpx : Acceleration.Tcg, LocalWindowsAccount = os.SelectedIndex == 1 && localAccount.IsOn, WindowsAutoSignIn = os.SelectedIndex == 1 && localAccount.IsOn && autoSignIn.IsOn, WindowsUserName = localUser.Text.Trim(), SetupPassword = localPassword.Password, SshEnabled = ssh.IsOn, SshPort = 2222, NetworkEnabled = network.IsOn, AcceleratedNetwork = !Native && fastNetwork.IsOn, AcceleratedGraphics = !Native && graphics.IsOn, DynamicDisplay = !Native && dynamic.IsOn, Engine = Native ? VmEngine.HyperV : VmEngine.Qemu, HyperVSwitch = Native ? nativeSwitch.SelectedItem as string ?? "" : "" };
    void ValidateStep()
    {
        if (step is 5 or 6) {
            if (ssh.IsOn && !network.IsOn) throw new InvalidDataException("Enable networking to use SSH.");
            if (os.SelectedIndex == 1 && localAccount.IsOn) GuestProvisioning.WindowsAnswerFile(localUser.Text.Trim(), localPassword.Password);
        }
        if (step == 0 && !(Native && hyperV.IsEnabled) && !(virtualize.IsChecked == true && virtualize.IsEnabled) && !(emulate.IsChecked == true && emulate.IsEnabled)) throw new InvalidDataException("Configure an available engine in Settings first.");
        if ((step == 4 || step == 6) && Native && network.IsOn && nativeSwitch.SelectedItem is null) throw new InvalidDataException("Choose an existing Hyper-V switch or turn networking off.");
        if (step == 1 || step == 6) { if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidDataException("Give your machine a name."); if (!File.Exists(iso.Text.Trim())) throw new InvalidDataException("Choose an existing installer ISO to continue."); }
        if (step == 2 || step == 6) { if (!double.IsFinite(cpu.Value) || !double.IsFinite(ram.Value) || cpu.Value != Math.Truncate(cpu.Value) || ram.Value != Math.Truncate(ram.Value)) throw new InvalidDataException("Enter whole numbers for CPU cores and memory."); HostProbe.ValidateAllocation(Configuration(), model.Host); }
        if (step == 3 || step == 6) { if (!double.IsFinite(disk.Value) || disk.Value < 1 || disk.Value > 2048 || disk.Value != Math.Truncate(disk.Value)) throw new InvalidDataException("Enter a whole disk capacity between 1 and 2048 GB."); }
    }
    async void OnContinue(ContentDialog sender, ContentDialogButtonClickEventArgs e)
    {
        e.Cancel = true; var deferral = e.GetDeferral();
        try
        {
            ValidateStep();
            if (step < 6) { step++; ShowStep(); return; }
            creating = true; IsPrimaryButtonEnabled = false; IsSecondaryButtonEnabled = false; PrimaryButtonText = "Creating…"; progress.IsIndeterminate = true; error.IsOpen = false;
            var vm = Configuration();
            if (vm.SshEnabled) { int sshPort = Native ? 22 : GuestProvisioning.AvailablePort(), agentPort = 0; if (!Native) do agentPort = GuestProvisioning.AvailablePort(); while (agentPort == sshPort); vm = vm with { SshPort = sshPort, SshAgentPort = agentPort }; }
            if (!Native && os.SelectedIndex == 1 && drivers.IsOn)
            {
                if (!VirtioDrivers.IsCached) { PrimaryButtonText = "Downloading drivers…"; progress.IsIndeterminate = false; progress.Value = 0; }
                vm = vm with { DriverIsoPath = await VirtioDrivers.EnsureAsync(new Progress<double>(fraction => progress.Value = fraction * progress.Maximum)) };
                PrimaryButtonText = "Creating…"; progress.IsIndeterminate = true;
            }
            await model.CreateAsync(vm); CreatedVm = model.Machines.Single(item => item.Id == vm.Id); creating = false; e.Cancel = false;
        }
        catch (Exception ex) { error.Message = ex.Message; error.IsOpen = true; }
        finally { creating = false; IsPrimaryButtonEnabled = true; IsSecondaryButtonEnabled = step > 0; PrimaryButtonText = step == 6 ? "Create machine" : "Continue"; progress.IsIndeterminate = false; progress.Value = step + 1; deferral.Complete(); }
    }
#if DEBUG
    internal void PreviewWindowsSetup() { os.SelectedIndex = 1; ssh.IsOn = true; hyperV.IsEnabled = true; hyperV.IsChecked = true; step = 5; ShowStep(); }
    internal void PreviewHyperVIntegration() { hyperV.IsEnabled = true; hyperV.IsChecked = true; step = 5; ShowStep(); }
#endif
}
