#if DEBUG
using System.Runtime.InteropServices.WindowsRuntime;
using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Aviary.App;

public sealed partial class MainWindow
{
    // Opt-in rendering harness. Uses synthetic fixtures, never the user's VM store.
    async Task ExportUiPreviewAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await WindowChrome.VerifyThemesAsync(this, root);
        model.Machines.Add(new() { Name = "CachyOS", OperatingSystem = "Linux", CpuCores = 4, MemoryMB = 4096, DiskGB = 32, DynamicDisplay = true });
        model.Machines.Add(new() { Name = "Ubuntu workspace", OperatingSystem = "Linux", CpuCores = 2, MemoryMB = 4096, DiskGB = 64 });
        model.Machines.Add(new() { Name = "Windows lab", OperatingSystem = "Windows", CpuCores = 4, MemoryMB = 8192, DiskGB = 80 });
        hostLabel.Text = "8 CPUs · 32 GB RAM\nHardware acceleration ready";
        ShowLibrary(); await SnapshotAsync("library");
        var initialSize = AppWindow.Size;
        foreach (int logicalWidth in new[] { 320, 480, 720, 1000 })
        {
            double scale = root.XamlRoot.RasterizationScale;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(logicalWidth * scale), (int)(700 * scale)));
            await Task.Delay(350); root.UpdateLayout();
            var header = (Microsoft.UI.Xaml.Controls.Grid)((Microsoft.UI.Xaml.Controls.Grid)content.Content).Children[0];
            var identity = (Microsoft.UI.Xaml.Controls.StackPanel)header.Children[0];
            var title = (Microsoft.UI.Xaml.Controls.TextBlock)identity.Children[0];
            if (title.ActualWidth < 160 || title.ActualHeight > 60) throw new InvalidOperationException("Library title collapsed at width " + logicalWidth);
            var actions = (FrameworkElement)header.Children[1];
            if (actions.ActualWidth > header.ActualWidth + 1) throw new InvalidOperationException("Library actions overflow at width " + logicalWidth);
            await SnapshotAsync("library-" + logicalWidth);
        }
        AppWindow.Resize(initialSize); await Task.Delay(350);
        ShowDetails(model.Machines[0]); await SnapshotAsync("details");
        ShowSettings(); await SnapshotAsync("settings");
        ShowLibrary();
        var wizard = new CreateVmDialog(this, model, root.XamlRoot);
        var shown = wizard.ShowAsync();
        await Task.Delay(300);
        await SnapshotAsync("wizard", wizard);
        wizard.PreviewHyperVIntegration(); await SnapshotAsync("hyperv-integration", wizard);
        wizard.PreviewWindowsSetup(); await SnapshotAsync("windows-setup", wizard);
        wizard.Hide(); await shown;
        model.Machines.Clear(); navigation.SelectedItem = libraryItem; ShowLibrary(); await SnapshotAsync("empty");
        if (Environment.GetEnvironmentVariable("AVIARY_QEMU") is { Length: > 0 } qemuPath)
        {
            var fixturePath = Path.Combine(directory, "vm-" + Guid.NewGuid());
            var installation = await QemuDiscovery.FindAsync(qemuPath) ?? throw new IOException("Preview QEMU was not found.");
            await using var backend = new QemuBackend(new VmStore(fixturePath), installation, model.Host);
            var vm = await backend.CreateAsync(new() { Name = "Alpine · display validation", CpuCores = 1, MemoryMB = 512, DiskGB = 1, DynamicDisplay = true, IsoPath = Environment.GetEnvironmentVariable("AVIARY_TEST_ISO") ?? "" });
            DisplayWindow? display = null;
            try
            {
                await backend.StartAsync(vm);
                display = new(vm.Name, true); display.Activate(); await display.ConnectAsync(backend.GetDisplay(vm.Id));
                await Task.Delay(1500); await display.VerifyFocusAsync(); await Task.Delay(1500);
                await SnapshotAsync("display", display.PreviewRoot);
                await display.VerifyFullscreenAsync(); display.PreviewFullscreen(); await SnapshotAsync("fullscreen", display.PreviewRoot);
            }
            finally { display?.Close(); await backend.DisposeAsync(); Directory.Delete(fixturePath, true); }
        }
        // Root rendering captures the real XAML layout, not a screen or a design mockup.
        await File.WriteAllTextAsync(Path.Combine(directory, "complete.txt"), "UI rendering completed. When AVIARY_QEMU is set, the live display and keyboard focus transfer were also verified.");
        Close();

        async Task SnapshotAsync(string name, UIElement? target = null)
        {
            await Task.Delay(250); root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(target ?? root);
            using var stream = File.Create(Path.Combine(directory, name + ".png"));
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
            var pixels = await bitmap.GetPixelsAsync();
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
        }
    }
}
#endif
