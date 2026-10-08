using System.Runtime.InteropServices.WindowsRuntime;
using Aviary.Core;
using Aviary.Qemu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Aviary.App;

public sealed partial class MainWindow : Window
{
    readonly LibraryViewModel model = new();
    readonly NavigationView navigation = new() { PaneDisplayMode = NavigationViewPaneDisplayMode.Auto, OpenPaneLength = 224, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsSettingsVisible = true, AlwaysShowHeader = false, IsPaneOpen = true };
    readonly Grid root = new();
    readonly Grid page = new() { Padding = new Thickness(32, 24, 32, 24), RowSpacing = 20 };
    readonly ContentControl content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly InfoBar notice = new() { IsOpen = false, IsClosable = true };
    readonly TextBlock hostLabel = Ui.Text("Checking your PC…", 12, true);
    readonly NavigationViewItem libraryItem = new() { Content = "All machines", Icon = new SymbolIcon(Symbol.AllApps), Tag = "library" };
    readonly NavigationViewItem runningItem = new() { Content = "Running", Icon = new SymbolIcon(Symbol.Play), Tag = "running" };
    readonly Dictionary<Guid, DisplayWindow> displays = [];
    readonly Dictionary<Guid, WriteableBitmap> previews = [];
    readonly Dictionary<Guid, Image> previewTargets = [];
    readonly HashSet<Guid> busy = [];
    TextBox search = new() { PlaceholderText = "Find a machine", Width = 220, VerticalAlignment = VerticalAlignment.Center };
    Guid? selected;
    ControlServer? control;
    bool runningOnly, initialized, closing, confirmingClose;
    GridView? libraryGrid;
    TextBlock? countLabel;

    public MainWindow()
    {
        Title = Branding.Name; WindowChrome.SizeForDisplay(this, 1240, 860); SystemBackdrop = new MicaBackdrop();
        WindowChrome.Apply(this, root);
        navigation.Resources["NavigationViewItemBackgroundSelected"] = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 71, 92));
        navigation.Resources["NavigationViewItemForegroundSelected"] = new SolidColorBrush(Microsoft.UI.Colors.White);
        navigation.PaneHeader = new Border { Padding = new Thickness(18, 28, 12, 24), Child = Ui.Stack(new Image { Source = new SvgImageSource(new Uri("ms-appx:///Assets/Aviary.svg")), Width = 40, Height = 40 }, Ui.Heading(Branding.Name, 24), Ui.Text("Your machines. Your space.", 12, true)) };
        navigation.MenuItems.Add(libraryItem); navigation.MenuItems.Add(runningItem);
        navigation.PaneFooter = new Border { Padding = new Thickness(20), Child = Ui.Stack(Ui.Text("THIS PC", 11, true), hostLabel) };
        page.RowDefinitions.Add(new() { Height = GridLength.Auto }); page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); page.Children.Add(notice); Grid.SetRow(content, 1); page.Children.Add(content);
        root.SizeChanged += (_, e) => page.Padding = e.NewSize.Width < 700 ? new Thickness(16, 16, 16, 16) : new Thickness(32, 24, 32, 24);
        navigation.Content = page; navigation.SelectedItem = libraryItem; root.Children.Add(navigation); Content = root;
        navigation.SelectionChanged += async (_, e) =>
        {
            if (e.IsSettingsSelected) { ShowSettings(); return; }
            runningOnly = ReferenceEquals(e.SelectedItem, runningItem); selected = null; ShowLibrary(); await Task.CompletedTask;
        };
        root.Loaded += async (_, _) =>
        {
            if (initialized) return; initialized = true;
#if DEBUG
            if (Environment.GetEnvironmentVariable("AVIARY_UI_PREVIEW") is { Length: > 0 } directory)
            {
                try { await ExportUiPreviewAsync(directory); }
                catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(directory, "error.txt"), ex.ToString()); Close(); }
                return;
            }
#endif
            await Guard(LoadAsync);
        };
        AppWindow.Closing += async (_, e) =>
        {
            if (closing || !model.Machines.Any(vm => vm.Engine == VmEngine.Qemu && State(vm).Status is VmStatus.Running or VmStatus.Paused or VmStatus.Starting or VmStatus.Stopping)) return; e.Cancel = true;
            if (confirmingClose) return; confirmingClose = true;
            try { if (await Confirm("Close Aviary?", "Running QEMU machines will be powered off. Shut down inside each guest first to preserve unsaved work. Hyper-V machines keep running in Windows.", "Power off and close")) { closing = true; foreach (var display in displays.Values.ToArray()) display.Close(); if (model.Backend is not null) await model.Backend.DisposeAsync(); Close(); } }
            finally { confirmingClose = false; }
        };
        Closed += async (_, _) => { if (control is not null) await control.DisposeAsync(); if (!closing && model.Backend is not null) await model.Backend.DisposeAsync(); };
    }
    bool ActiveMachines() => model.Machines.Any(vm => State(vm).Status is VmStatus.Running or VmStatus.Paused or VmStatus.Starting or VmStatus.Stopping);
    VmState State(VmConfiguration vm) => model.Backend?.GetStatus(vm.Id) ?? new(vm.Id, VmStatus.Stopped);
    Button Action(string label, string glyph, Func<Task> action, bool primary = false) => Ui.Action(label, glyph, () => Guard(action), primary);
    async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { notice.Title = "Something needs your attention"; notice.Message = ex.Message; notice.Severity = InfoBarSeverity.Error; notice.IsOpen = true; }
    }
    async Task<bool> Confirm(string title, string text, string accept) => await new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = Ui.Text(text), PrimaryButtonText = accept, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
    async Task LoadAsync()
    {
        content.Content = new ProgressRing { IsActive = true, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        await model.InitializeAsync();
        if (model.Backend is not null) model.Backend.StateChanged += state => DispatcherQueue.TryEnqueue(() => OnStateChanged(state));
        // Serves aviary-mcp. Recreated with the library whenever settings reload it.
        if (control is not null) await control.DisposeAsync();
        control = model.Backend is null ? null : new ControlServer(new ControlService(new AppLibrary(model, DispatcherQueue, vm => { if (selected == vm.Id) ShowDetails(vm); })));
        hostLabel.Text = $"{model.Host.LogicalCpuCount} CPUs · {Ui.Memory((int)model.Host.MemoryMB)} RAM\n" + (model.Host.WhpxAvailable ? "Hardware acceleration ready" : "Software emulation available");
        if (model.Installation is null && !model.HyperV.Available) { notice.Title = "Connect your virtualization engine"; notice.Message = "Choose QEMU or check Hyper-V availability in Settings."; notice.Severity = InfoBarSeverity.Warning; notice.IsOpen = true; }
        else if (model.LoadErrors.Count > 0) { notice.Title = "Some machines could not be loaded"; notice.Message = string.Join("\n", model.LoadErrors); notice.Severity = InfoBarSeverity.Warning; notice.IsOpen = true; }
        if (navigation.SelectedItem == navigation.SettingsItem) ShowSettings(); else ShowLibrary();
    }
    void OnStateChanged(VmState state)
    {
        if (state.Status is VmStatus.Stopped or VmStatus.Error && displays.Remove(state.Id, out var display)) display.Close();
        if (selected is { } id && model.Machines.FirstOrDefault(v => v.Id == id) is { } vm) ShowDetails(vm);
        else if (navigation.SelectedItem != navigation.SettingsItem) PopulateLibrary();
    }
    void ShowLibrary()
    {
        selected = null; previewTargets.Clear();
        var layout = new Grid { RowSpacing = 24 }; layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { ColumnSpacing = 12 }; header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.RowDefinitions.Add(new() { Height = GridLength.Auto }); header.RowDefinitions.Add(new() { Height = GridLength.Auto }); header.RowSpacing = 16;
        var title = Ui.Heading(runningOnly ? "Running machines" : "Your library", 32);
        title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        countLabel = Ui.Text("", 13, true); var identity = Ui.Stack(title, countLabel); header.Children.Add(identity);
        // Each library view owns its search control; reparenting a retained WinUI
        // TextBox after navigation can fail while its old tree is unloading.
        var searchText = search.Text;
        var searchBox = new TextBox { PlaceholderText = "Find a machine", Width = 220, Text = searchText, VerticalAlignment = VerticalAlignment.Center };
        search = searchBox; searchBox.TextChanged += (_, _) => PopulateLibrary();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; actions.Children.Add(searchBox); actions.Children.Add(Action("Create machine", "\uE710", CreateAsync, true)); Grid.SetColumn(actions, 1); header.Children.Add(actions); layout.Children.Add(header);
        void ArrangeHeader(double available)
        {
            bool stacked = available < 720;
            Grid.SetRow(actions, stacked ? 1 : 0); Grid.SetColumn(actions, stacked ? 0 : 1);
            Grid.SetColumnSpan(actions, stacked ? 2 : 1); Grid.SetColumnSpan(identity, stacked ? 2 : 1);
            actions.Orientation = available < 400 ? Orientation.Vertical : Orientation.Horizontal;
            actions.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            searchBox.Width = available < 400 ? double.NaN : 220;
            searchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        header.SizeChanged += (_, e) => ArrangeHeader(e.NewSize.Width); ArrangeHeader(page.ActualWidth);
        libraryGrid = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 0, 0, 20) };
        libraryGrid.SizeChanged += (_, _) => SizeLibraryCards();
        libraryGrid.ItemClick += (_, e) => { if (e.ClickedItem is FrameworkElement { Tag: VmConfiguration vm }) ShowDetails(vm); };
        Grid.SetRow(libraryGrid, 1); layout.Children.Add(libraryGrid); content.Content = layout; PopulateLibrary();
    }
    void PopulateLibrary()
    {
        if (libraryGrid is null || selected is not null) return;
        libraryGrid.Items.Clear(); previewTargets.Clear();
        var machines = model.Machines.Where(vm => (!runningOnly || State(vm).Status is VmStatus.Running or VmStatus.Paused) && (vm.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || vm.OperatingSystem.Contains(search.Text, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (countLabel is not null) countLabel.Text = $"{model.Machines.Count} machines · {model.Machines.Count(v => State(v).Status == VmStatus.Running)} running";
        if (machines.Length == 0)
        {
            var title = model.Machines.Count == 0 ? "Make room for something new" : runningOnly ? "A quiet moment" : "No matching machines";
            var explanation = model.Machines.Count == 0 ? "Try a new operating system, build a test environment, or keep a workspace of your own." : runningOnly ? "Start a machine from your library and it will appear here." : "Try a different name or operating system.";
            libraryGrid.Items.Add(new Border { MaxWidth = 480, Width = Math.Max(100, Math.Min(480, libraryGrid.ActualWidth - 24)), Padding = new Thickness(32, 64, 32, 48), Child = Ui.Stack(Ui.OsIcon("Other", 80), Ui.Heading(title, 24), Ui.Text(explanation, 15, true), Action(model.Machines.Count == 0 ? "Create your first machine" : "View all machines", "\uE710", model.Machines.Count == 0 ? CreateAsync : () => { search.Text = ""; navigation.SelectedItem = libraryItem; return Task.CompletedTask; }, true)) }); return;
        }
        foreach (var vm in machines)
        {
            var state = State(vm); var body = new StackPanel { Spacing = 14 };
            var preview = Preview(vm, 156); body.Children.Add(preview);
            var title = Ui.Heading(vm.Name, 18); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; body.Children.Add(title);
            body.Children.Add(Ui.Text($"{vm.OperatingSystem} · {(vm.Engine == VmEngine.HyperV ? "Hyper-V" : "QEMU")} · {(vm.Architecture == GuestArchitecture.X86_64 ? "64-bit" : "32-bit")}", 12, true));
            body.Children.Add(Ui.Text($"{vm.CpuCores} CPUs   ·   {Ui.Memory(vm.MemoryMB)} RAM   ·   {vm.DiskGB} GB", 12, true));
            var footer = new Grid(); footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.Children.Add(Ui.Status(state.Status));
            var go = PrimaryAction(vm); Grid.SetColumn(go, 1); footer.Children.Add(go); body.Children.Add(footer);
            var card = Ui.Card(body, 18); card.Width = 290; card.Margin = new Thickness(0, 0, 14, 14); card.Tag = vm; card.ContextFlyout = MachineMenu(vm); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, vm.Name + ", " + state.Status); libraryGrid.Items.Add(card);
        }
        SizeLibraryCards();
    }
    void SizeLibraryCards()
    {
        if (libraryGrid is null || libraryGrid.ActualWidth <= 0) return;
        foreach (var item in libraryGrid.Items.OfType<FrameworkElement>())
            item.Width = Math.Max(100, Math.Min(item.Tag is VmConfiguration ? 290 : 480, libraryGrid.ActualWidth - 24));
    }
    FrameworkElement Preview(VmConfiguration vm, double height)
    {
        var color = Ui.OsColor(vm.OperatingSystem);
        var backdrop = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        backdrop.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(35, color.R, color.G, color.B), Offset = 0 });
        backdrop.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(8, color.R, color.G, color.B), Offset = 1 });
        var area = new Grid { Height = height, Background = backdrop, CornerRadius = new CornerRadius(8) };
        var icon = Ui.OsIcon(vm.OperatingSystem, height > 200 ? 80 : 56); icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center; area.Children.Add(icon);
        var view = new Image { Stretch = Stretch.Uniform }; if (previews.TryGetValue(vm.Id, out var frame)) view.Source = frame; area.Children.Add(view); previewTargets[vm.Id] = view;
        return area;
    }
    Button PrimaryAction(VmConfiguration vm)
    {
        var state = State(vm).Status; bool active = state is VmStatus.Running or VmStatus.Paused;
        var button = Action(active ? "Open" : state == VmStatus.Starting ? "Starting…" : state == VmStatus.Stopping ? "Stopping…" : "Start", active ? "\uE8A7" : "\uE768", () => StartOrOpen(vm), true);
        button.IsEnabled = !busy.Contains(vm.Id) && state is not (VmStatus.Starting or VmStatus.Stopping) && model.Backend?.Available(vm.Engine) == true; return button;
    }
    async Task StartOrOpen(VmConfiguration vm)
    {
        if (!busy.Add(vm.Id)) return;
        try { await VmStartup.StartOrOpenAsync(vm, State(vm).Status, () => (model.Backend ?? throw new InvalidOperationException("Configure an available engine in Settings.")).StartAsync(vm), () => OpenDisplay(vm)); }
        finally { busy.Remove(vm.Id); if (selected is not null) ShowDetails(vm); else PopulateLibrary(); }
    }
    async Task OpenDisplay(VmConfiguration vm)
    {
        if (vm.Engine == VmEngine.HyperV) { await model.Backend!.OpenNativeConsoleAsync(vm.Id); return; }
        if (displays.TryGetValue(vm.Id, out var existing)) { existing.Activate(); return; }
        var window = new DisplayWindow(vm.Name, vm.DynamicDisplay, () => model.Backend!.StopAsync(vm.Id)); displays[vm.Id] = window;
        window.Closed += (_, _) => displays.Remove(vm.Id);
        window.PreviewUpdated += (w, h, pixels) =>
        {
            if (!previews.TryGetValue(vm.Id, out var preview) || preview.PixelWidth != w || preview.PixelHeight != h) { preview = new(w, h); previews[vm.Id] = preview; }
            using var buffer = preview.PixelBuffer.AsStream(); buffer.Write(pixels); preview.Invalidate();
            if (previewTargets.TryGetValue(vm.Id, out var target)) target.Source = preview;
        };
        window.Activate(); try { await window.ConnectAsync(model.Backend!.GetDisplay(vm.Id)); } catch { window.Close(); throw; }
    }
    async Task CreateAsync()
    {
        if (model.Backend is null) { navigation.SelectedItem = navigation.SettingsItem; ShowSettings(); return; }
        var dialog = new CreateVmDialog(this, model, root.XamlRoot); await dialog.ShowAsync(); if (dialog.CreatedVm is { } vm) ShowDetails(vm);
    }
}

