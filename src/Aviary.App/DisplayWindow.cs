using System.Runtime.InteropServices.WindowsRuntime;
using Aviary.Core;
using Aviary.Qemu;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Aviary.App;

public sealed class GuestSurface : Grid, IDisposable
{
    readonly InputSystemCursor arrow = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    readonly InputCursor transparent = GuestCursor.CreateTransparent();
    public void ShowHostCursor(bool show) => ProtectedCursor = show ? arrow : transparent;
    public void Dispose() { ProtectedCursor = null; transparent.Dispose(); arrow.Dispose(); }
#if DEBUG
    internal void VerifyCursorSelection()
    {
        ShowHostCursor(false);
        if (ProtectedCursor is null || ProtectedCursor is InputSystemCursor) throw new InvalidOperationException("The guest surface must use an explicit transparent cursor, not an inherited system pointer.");
        ShowHostCursor(true);
        if (!ReferenceEquals(ProtectedCursor, arrow)) throw new InvalidOperationException("The host pointer was not restored.");
    }
#endif
}

public sealed class DisplayWindow : Window
{
    readonly VncDisplayClient client = new();
    readonly GuestKeyboard keyboard = new();
    readonly GuestSurface surface = new() { IsTabStop = true, Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
    readonly Image image = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    readonly Image cursor = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Canvas cursorLayer = new() { IsHitTestVisible = false };
    readonly ScrollViewer viewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = false };
    readonly TextBlock status = Ui.Text("Connecting to your machine…", 12, true);
    readonly TextBlock resolution = Ui.Text("", 12, true);
    readonly InfoBar message = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Informational };
    readonly ComboBox scaleMode = new() { ItemsSource = new[] { "Fit to window", "Actual size" }, SelectedIndex = 0, MinWidth = 150 };
    readonly ToggleSwitch resizeGuest = new() { OnContent = "Auto resolution", OffContent = "Auto resolution", VerticalAlignment = VerticalAlignment.Center };
    readonly Button fullscreen;
    readonly Grid root = new();
    readonly Grid toolbar;
    readonly Grid footer;
    readonly Border fullscreenDock = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(0, 0, 10, 10), Visibility = Visibility.Collapsed };
    readonly Border topEdge = new() { Height = 5, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Visibility = Visibility.Collapsed };
    readonly Button leaveFullscreen;
    bool fullscreenShortcut;
    readonly CancellationTokenSource lifetime = new();
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer resizeTimer;
    Task inputTail = Task.CompletedTask;
    WriteableBitmap? bitmap;
    (int W, int H, byte[] Pixels)? pendingFrame;
    readonly object frameLock = new();
    bool frameQueued, connected, closed, fullScreen, pointerInside;
    byte buttons;
    int width = 1, height = 1, cursorW, cursorH, hotspotX, hotspotY;
    Point pointerPosition;
    GuestPointer lastPointer;
    (int W, int H) lastResize;
    DateTime lastPreview;
    public event Action<int, int, byte[]>? PreviewUpdated;

    public DisplayWindow(string name, bool dynamicDisplay = false, Func<Task>? shutdown = null)
    {
        Title = name + " — " + Branding.Name; AppWindow.Resize(new Windows.Graphics.SizeInt32(1160, 800)); SystemBackdrop = new MicaBackdrop();
        WindowChrome.Apply(this, root);
        root.Background = Ui.Brush("ApplicationPageBackgroundThemeBrush");
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        toolbar = new Grid { Padding = new Thickness(16, 10, 16, 10), ColumnSpacing = 16 };
        toolbar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); toolbar.Children.Add(Ui.Heading(name, 16));
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; controls.Children.Add(scaleMode);
        resizeGuest.IsEnabled = dynamicDisplay; resizeGuest.IsOn = dynamicDisplay;
        ToolTipService.SetToolTip(resizeGuest, dynamicDisplay ? "Ask the guest desktop to follow this window’s size. Requires its virtio display driver." : "Enable adaptive display in this VM’s settings while it is stopped."); controls.Children.Add(resizeGuest);
        fullscreen = Ui.Action("Fullscreen", "\uE740", () => { SetFullscreen(!fullScreen); return Task.CompletedTask; }); controls.Children.Add(fullscreen);
        var more = new Button { Content = Ui.Icon("\uE712"), Padding = new Thickness(10) }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, "VM controls");
        var menu = new MenuFlyout(); var cad = new MenuFlyoutItem { Text = "Send Ctrl+Alt+Delete" }; cad.Click += (_, _) => QueueInput(client.SendCtrlAltDeleteAsync); menu.Items.Add(cad);
        if (shutdown is not null) { var power = new MenuFlyoutItem { Text = "Shut down guest", Icon = new SymbolIcon(Symbol.Stop) }; power.Click += async (_, _) => { try { await shutdown(); } catch (Exception ex) { ShowMessage(ex.Message); } }; menu.Items.Add(power); }
        more.Flyout = menu; controls.Children.Add(more); Grid.SetColumn(controls, 1); toolbar.Children.Add(controls); root.Children.Add(toolbar);
        Grid.SetRow(message, 1); root.Children.Add(message);
        surface.Children.Add(image); cursorLayer.Children.Add(cursor); surface.Children.Add(cursorLayer); viewport.Content = surface; Grid.SetRow(viewport, 2); root.Children.Add(viewport);
        footer = new Grid { Padding = new Thickness(16, 8, 16, 8), ColumnSpacing = 16 }; footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.Children.Add(status); Grid.SetColumn(resolution, 1); footer.Children.Add(resolution); Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        fullscreenDock.Background = Ui.Brush("ApplicationPageBackgroundThemeBrush");
        leaveFullscreen = Ui.Action("Exit fullscreen", "\uE73F", () => { SetFullscreen(false); return Task.CompletedTask; });
        fullscreenDock.Child = Ui.Stack(Ui.Text(name + " · Ctrl+Alt+Enter to exit", 12), leaveFullscreen);
        Grid.SetRowSpan(topEdge, 4); Grid.SetRowSpan(fullscreenDock, 4); Canvas.SetZIndex(topEdge, 10); Canvas.SetZIndex(fullscreenDock, 11);
        root.Children.Add(topEdge); root.Children.Add(fullscreenDock);
        topEdge.PointerEntered += (_, _) => { if (fullScreen) fullscreenDock.Visibility = Visibility.Visible; };
        fullscreenDock.PointerExited += (_, _) => { if (fullScreen && keyboard.Focused) fullscreenDock.Visibility = Visibility.Collapsed; };
        root.PreviewKeyDown += (_, e) =>
        {
            bool Held(Windows.System.VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (e.Key == Windows.System.VirtualKey.Enter && Held(Windows.System.VirtualKey.Control) && Held(Windows.System.VirtualKey.Menu))
            { e.Handled = true; if (!fullscreenShortcut) { fullscreenShortcut = true; SetFullscreen(!fullScreen); } }
        };
        root.PreviewKeyUp += (_, e) => { if (fullscreenShortcut && e.Key == Windows.System.VirtualKey.Enter) { fullscreenShortcut = false; e.Handled = true; } };
        resizeTimer = DispatcherQueue.CreateTimer(); resizeTimer.Interval = TimeSpan.FromMilliseconds(350); resizeTimer.IsRepeating = false; resizeTimer.Tick += (_, _) => RequestGuestSize();
        viewport.SizeChanged += (_, _) => { LayoutDisplay(); ScheduleResize(); }; scaleMode.SelectionChanged += (_, _) => { ReleaseInput(); LayoutDisplay(); }; resizeGuest.Toggled += (_, _) => ScheduleResize();
        surface.GotFocus += (_, _) => { if (connected) { keyboard.Focus(); UpdateStatus(); } }; surface.LostFocus += (_, _) => ReleaseInput();
        // Preview intercepts guest Tab/arrows before XAML focus navigation.
        surface.PreviewKeyDown += (_, e) => HandleKey(e, true); surface.PreviewKeyUp += (_, e) => HandleKey(e, false);
        surface.PointerPressed += (_, e) => { if (!connected || Map(e.GetCurrentPoint(surface).Position) is null) return; surface.Focus(FocusState.Pointer); keyboard.Focus(); surface.CapturePointer(e.Pointer); HandlePointer(e); e.Handled = true; UpdateStatus(); };
        surface.PointerEntered += (_, e) => HandlePointer(e);
        surface.PointerMoved += (_, e) => { if (fullScreen && e.GetCurrentPoint(root).Position.Y > 100 && keyboard.Focused) fullscreenDock.Visibility = Visibility.Collapsed; HandlePointer(e); };
        surface.PointerReleased += (_, e) => { HandlePointer(e); surface.ReleasePointerCapture(e.Pointer); e.Handled = connected; };
        surface.PointerCaptureLost += (_, _) => ReleaseButtons(); surface.PointerCanceled += (_, _) => { ReleaseButtons(); RestoreHostPointer(); };
        surface.PointerExited += (_, _) => RestoreHostPointer();
        surface.PointerWheelChanged += (_, e) => { if (!connected || Map(e.GetCurrentPoint(surface).Position) is null) return; HandlePointer(e, e.GetCurrentPoint(surface).Properties.MouseWheelDelta > 0 ? (byte)8 : (byte)16); HandlePointer(e); e.Handled = true; };
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) { fullscreenShortcut = false; ReleaseInput(); RestoreHostPointer(); } else if (connected && ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), surface)) { keyboard.Focus(); UpdateStatus(); } };
        client.FrameReceived += ReceiveFrame;
        client.CursorChanged += (w, h, x, y, data) => DispatcherQueue.TryEnqueue(() => SetCursor(w, h, x, y, data));
        client.ResizeReply += code => DispatcherQueue.TryEnqueue(() => { if (code is 1 or 2 or 3) { resizeGuest.IsOn = false; ShowMessage("The guest did not accept automatic resizing. Fit to window is still available; enable the guest’s virtio display driver to resize its desktop."); } });
        client.Disconnected += error => DispatcherQueue.TryEnqueue(() => { if (closed) return; connected = false; keyboard.Blur(); RestoreHostPointer(); status.Text = "Display disconnected"; ShowMessage(error); });
        Closed += async (_, _) => { ReleaseInput(); RestoreHostPointer(); closed = true; connected = false; resizeTimer.Stop(); lifetime.Cancel(); await inputTail; await client.DisposeAsync(); surface.Dispose(); lifetime.Dispose(); };
    }
    public async Task ConnectAsync(IDisplayConnection connection)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(connection, timeout.Token); connected = true; UpdateStatus(); ScheduleResize();
    }
    void HandleKey(KeyRoutedEventArgs e, bool down)
    {
        if (!connected || e.Handled) return;
        var result = down ? keyboard.Down((int)e.Key, (int)e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey) : keyboard.Up((int)e.Key, (int)e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
        e.Handled = result.Handled; SendKeys(result.Events);
        if (result.ReleaseFocus) { ReleaseButtons(); if (fullScreen) { fullscreenDock.Visibility = Visibility.Visible; leaveFullscreen.Focus(FocusState.Keyboard); } else fullscreen.Focus(FocusState.Keyboard); UpdateStatus(); }
    }
    void SetFullscreen(bool enabled)
    {
        ReleaseInput();
        AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        fullScreen = enabled;
        toolbar.Visibility = footer.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        topEdge.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        fullscreenDock.Visibility = Visibility.Collapsed;
        if (enabled) message.IsOpen = false;
        if (surface.Focus(FocusState.Programmatic) && connected) keyboard.Focus();
        UpdateStatus(); LayoutDisplay(); ScheduleResize();
    }
    void SendKeys(IReadOnlyList<GuestKeyEvent> events) { if (events.Count != 0) QueueInput(async () => { foreach (var key in events) await client.KeyAsync(key.Symbol, key.Down); }); }
    void ReleaseInput() { SendKeys(keyboard.Blur()); ReleaseButtons(); if (!closed) UpdateStatus(); }
    void ReleaseButtons() { if (buttons == 0) return; buttons = 0; var position = lastPointer; QueueInput(() => client.PointerAsync(position.X, position.Y, 0)); }
    void QueueInput(Func<Task> operation) { if (connected && !closed) inputTail = SendAfterAsync(inputTail, operation); }
    async Task SendAfterAsync(Task previous, Func<Task> operation)
    {
        await previous;
        try { await operation(); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { if (!closed) { status.Text = "Input disconnected"; ShowMessage(ex.Message); } }
    }
    void UpdateStatus() => status.Text = !connected ? "Connecting to your machine…" : keyboard.Focused ? "Typing in guest · Ctrl+Alt+G returns to toolbar" : "Click the desktop to type · Mouse moves freely between windows";
    void ShowMessage(string text) { if (fullScreen) SetFullscreen(false); message.Message = text; message.IsOpen = true; }
    GuestPointer? Map(Point position, bool clamp = false)
    {
        var local = surface.TransformToVisual(image).TransformPoint(position);
        return DisplayCoordinates.Map(local.X, local.Y, image.ActualWidth, image.ActualHeight, width, height, clamp);
    }
    void HandlePointer(PointerRoutedEventArgs e, byte wheel = 0)
    {
        if (!connected || closed) return;
        var point = e.GetCurrentPoint(surface); pointerPosition = point.Position;
        var mapped = Map(point.Position, buttons != 0); pointerInside = Map(point.Position) is not null;
        surface.ShowHostCursor(!pointerInside); MoveCursor();
        if (mapped is not { } position) return; lastPointer = position;
        buttons = (byte)((point.Properties.IsLeftButtonPressed ? 1 : 0) | (point.Properties.IsMiddleButtonPressed ? 2 : 0) | (point.Properties.IsRightButtonPressed ? 4 : 0)); var mask = (byte)(buttons | wheel);
        QueueInput(() => client.PointerAsync(position.X, position.Y, mask));
    }
    void RestoreHostPointer() { pointerInside = false; surface.ShowHostCursor(true); cursor.Visibility = Visibility.Collapsed; }
    void SetCursor(int w, int h, int x, int y, byte[] pixels)
    {
        if (closed) return; cursorW = w; cursorH = h; hotspotX = x; hotspotY = y;
        if (w == 0 || h == 0) { cursor.Source = null; cursor.Visibility = Visibility.Collapsed; return; }
        var source = new WriteableBitmap(w, h); using var buffer = source.PixelBuffer.AsStream(); buffer.Write(pixels); source.Invalidate(); cursor.Source = source; MoveCursor();
    }
    void MoveCursor()
    {
        double scale = Math.Min(image.ActualWidth / width, image.ActualHeight / height);
        cursor.Width = cursorW * scale; cursor.Height = cursorH * scale; Canvas.SetLeft(cursor, pointerPosition.X - hotspotX * scale); Canvas.SetTop(cursor, pointerPosition.Y - hotspotY * scale);
        cursor.Visibility = pointerInside && cursor.Source is not null ? Visibility.Visible : Visibility.Collapsed;
    }
    void ReceiveFrame(int w, int h, byte[] pixels)
    {
        lock (frameLock) { pendingFrame = (w, h, pixels); if (frameQueued) return; frameQueued = true; }
        DispatcherQueue.TryEnqueue(() =>
        {
            (int W, int H, byte[] Pixels)? frame; lock (frameLock) { frame = pendingFrame; pendingFrame = null; frameQueued = false; }
            if (closed || frame is not { } next) return; bool changed = width != next.W || height != next.H; width = next.W; height = next.H;
            if (bitmap is null || changed) { bitmap = new(width, height); image.Source = bitmap; LayoutDisplay(); }
            using var buffer = bitmap.PixelBuffer.AsStream(); buffer.Write(next.Pixels); bitmap.Invalidate(); resolution.Text = $"{width} × {height}";
            if (DateTime.UtcNow - lastPreview > TimeSpan.FromSeconds(1)) { lastPreview = DateTime.UtcNow; PreviewUpdated?.Invoke(width, height, next.Pixels); }
        });
    }
    void LayoutDisplay()
    {
        bool actual = scaleMode.SelectedIndex == 1; double dpi = root.XamlRoot?.RasterizationScale ?? 1;
        surface.Width = actual ? Math.Max(viewport.ActualWidth, width / dpi) : viewport.ActualWidth; surface.Height = actual ? Math.Max(viewport.ActualHeight, height / dpi) : viewport.ActualHeight;
        image.Stretch = actual ? Stretch.Fill : Stretch.Uniform; image.Width = actual ? width / dpi : double.NaN; image.Height = actual ? height / dpi : double.NaN;
        image.HorizontalAlignment = actual ? HorizontalAlignment.Center : HorizontalAlignment.Stretch; image.VerticalAlignment = actual ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        viewport.HorizontalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled; viewport.VerticalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled; MoveCursor();
    }
    void ScheduleResize() { if (connected && resizeGuest.IsOn && !closed) { resizeTimer.Stop(); resizeTimer.Start(); } }
    void RequestGuestSize()
    {
        if (!connected || !resizeGuest.IsOn || closed || viewport.ActualWidth <= 0) return;
        double dpi = root.XamlRoot?.RasterizationScale ?? 1; int w = Math.Clamp((int)(viewport.ActualWidth * dpi) / 8 * 8, 320, 4096); int h = Math.Clamp((int)(viewport.ActualHeight * dpi) / 8 * 8, 200, 2160);
        if (lastResize == (w, h)) return; lastResize = (w, h); QueueInput(() => client.ResizeDesktopAsync(w, h));
    }
#if DEBUG
    internal UIElement PreviewRoot => root;
    internal async Task VerifyFullscreenAsync()
    {
        SetFullscreen(true); await Task.Delay(300);
        if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen || toolbar.Visibility != Visibility.Collapsed || footer.Visibility != Visibility.Collapsed || fullscreenDock.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Fullscreen chrome is still visible.");
        if (Math.Abs(viewport.ActualHeight - root.ActualHeight) > 1) throw new InvalidOperationException("Guest does not fill fullscreen height.");
        SetFullscreen(false); await Task.Delay(300);
        if (toolbar.Visibility != Visibility.Visible || footer.Visibility != Visibility.Visible || !keyboard.Focused) throw new InvalidOperationException("Fullscreen did not restore the guest window and focus.");
    }
    internal void PreviewFullscreen() => SetFullscreen(true);
    internal async Task VerifyFocusAsync()
    {
        surface.VerifyCursorSelection();
        surface.Focus(FocusState.Programmatic); await Task.Delay(50);
        if (!keyboard.Focused) throw new InvalidOperationException("Guest surface did not acquire keyboard focus.");
        SendKeys(keyboard.Down(0x11).Events);
        if (!fullscreen.Focus(FocusState.Programmatic)) throw new InvalidOperationException("Toolbar could not acquire focus.");
        await Task.Delay(50); await inputTail;
        if (keyboard.Focused || keyboard.Up(0x11).Events.Count != 0) throw new InvalidOperationException("Moving to the toolbar did not release guest modifiers.");
        surface.Focus(FocusState.Programmatic); await Task.Delay(50);
        if (!keyboard.Focused) throw new InvalidOperationException("Guest focus was not restored without a capture switch.");
        ReleaseInput();
    }
#endif
}

