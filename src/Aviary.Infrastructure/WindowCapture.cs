using System.Runtime.InteropServices;

namespace Aviary.Infrastructure;

// For guests that draw in QEMU's own window (3D on Windows hosts), where QMP screendump can't read the GL scanout.
public static class WindowCapture
{
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant; }

    [DllImport("user32.dll")] static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] static extern nint GetDC(nint window);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);

    // PW_CLIENTONLY | PW_RENDERFULLCONTENT: the client area, including GPU-composed (DirectX/OpenGL) content.
    public static byte[] ClientAreaPng(nint window)
    {
        if (window == 0) throw new InvalidOperationException("The machine's window isn't open.");
        if (IsIconic(window)) throw new InvalidOperationException("The machine's window is minimized. Restore it to take a screenshot.");
        if (!GetClientRect(window, out var rect) || rect.Right <= 0 || rect.Bottom <= 0) throw new InvalidOperationException("The machine's window has no visible area.");
        int width = rect.Right, height = rect.Bottom;
        var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), bitmap = CreateDIBSection(screen, ref header, 0, out var bits, 0, 0);
        try
        {
            var previous = SelectObject(memory, bitmap);
            if (!PrintWindow(window, memory, 1 | 2)) throw new InvalidOperationException("Windows couldn't capture the machine's window.");
            SelectObject(memory, previous);
            var bgra = new byte[width * height * 4]; Marshal.Copy(bits, bgra, 0, bgra.Length);
            var rgb = new byte[width * height * 3];
            for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3) { rgb[j] = bgra[i + 2]; rgb[j + 1] = bgra[i + 1]; rgb[j + 2] = bgra[i]; }
            return Png.FromRgb(rgb, width, height);
        }
        finally { DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, screen); }
    }

    public static void BringToFront(nint window)
    {
        if (window == 0) throw new InvalidOperationException("The machine's window isn't open yet.");
        if (IsIconic(window)) ShowWindow(window, 9); // SW_RESTORE
        SetForegroundWindow(window);
    }
}
