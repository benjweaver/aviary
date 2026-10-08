using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Aviary.App;

internal static class WindowChrome
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(nint window);
    public static void SizeForDisplay(Window window, int width, int height)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        double scale = Math.Max(96, GetDpiForWindow(handle)) / 96d;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min((int)(width * scale), (int)(area.Width * .9)), Math.Min((int)(height * scale), (int)(area.Height * .9))));
    }

    public static void Apply(Window window, FrameworkElement content)
    {
        // Keep the Windows caption, system menu, Snap layouts, and accessibility behavior.
        window.ExtendsContentIntoTitleBar = false;
        window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Aviary.ico"));
        void Update()
        {
            int dark = content.ActualTheme == ElementTheme.Dark ? 1 : 0;
            // An unsupported attribute on older Windows safely retains the native default.
            _ = DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(window), 20, ref dark, sizeof(int));
        }
        content.ActualThemeChanged += (_, _) => Update();
        content.Loaded += (_, _) => Update();
        window.Activated += (_, _) => Update();
        Update();
    }
#if DEBUG
    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    internal static async Task VerifyThemesAsync(Window window, FrameworkElement content)
    {
        var original = content.RequestedTheme;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                content.RequestedTheme = theme; await Task.Delay(100);
                int result = DwmGetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(window), 20, out var dark, sizeof(int));
                if (result == 0 && dark != (theme == ElementTheme.Dark ? 1 : 0)) throw new InvalidOperationException("Native title bar did not follow the window theme.");
            }
            if (window.ExtendsContentIntoTitleBar) throw new InvalidOperationException("Native Windows caption was replaced.");
        }
        finally { content.RequestedTheme = original; }
    }
#endif
}
