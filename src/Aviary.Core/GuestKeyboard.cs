namespace Aviary.Core;

public readonly record struct GuestKeyEvent(uint Symbol, bool Down);
public sealed record GuestKeyResult(bool Handled, bool ReleaseFocus, IReadOnlyList<GuestKeyEvent> Events);

/// <summary>Tracks the physical keys sent to a guest, independent of UI and transport.</summary>
public sealed class GuestKeyboard
{
    readonly Dictionary<int, uint> pressed = [];
    public bool Focused { get; private set; }
    public void Focus() => Focused = true;

    public IReadOnlyList<GuestKeyEvent> Blur()
    {
        Focused = false;
        var releases = pressed.Values.Reverse().Select(symbol => new GuestKeyEvent(symbol, false)).ToArray();
        pressed.Clear();
        return releases;
    }

    public GuestKeyResult Down(int virtualKey, int scanCode = 0, bool extended = false)
    {
        if (!Focused) return new(false, false, []);
        // Ctrl+Alt+G is reserved for returning focus to the VM toolbar.
        if (virtualKey == 0x47 && pressed.Values.Any(k => k is 0xffe3 or 0xffe4) && pressed.Values.Any(k => k is 0xffe9 or 0xffea))
            return new(true, true, Blur());
        // Let Windows handle system task switching. Release guest modifiers first.
        if ((virtualKey == 0x09 && pressed.Values.Any(k => k is 0xffe9 or 0xffea)) || virtualKey is 0x5b or 0x5c)
            return new(false, true, Blur());
        int identity = Identity(virtualKey, scanCode, extended);
        uint symbol = pressed.GetValueOrDefault(identity, Symbol(virtualKey, scanCode, extended));
        if (symbol == 0) return new(false, false, []);
        pressed[identity] = symbol;
        return new(true, false, [new(symbol, true)]);
    }

    public GuestKeyResult Up(int virtualKey, int scanCode = 0, bool extended = false)
    {
        if (!pressed.Remove(Identity(virtualKey, scanCode, extended), out var symbol)) return new(false, false, []);
        return new(true, false, [new(symbol, false)]);
    }

    static int Identity(int key, int scan, bool extended) => scan == 0 ? key | (extended ? 0x10000 : 0) : scan | 0x20000 | (extended ? 0x10000 : 0);

    public static uint Symbol(int key, int scan = 0, bool extended = false)
    {
        if (key is >= 0x41 and <= 0x5a) return (uint)key + 32;
        if (key is >= 0x30 and <= 0x39) return (uint)key;
        if (key is >= 0x70 and <= 0x87) return 0xffbe + (uint)key - 0x70;
        if (key is >= 0x60 and <= 0x69) return 0xffb0 + (uint)key - 0x60;
        return key switch
        {
            0x08 => 0xff08, 0x09 => 0xff09, 0x0d => extended ? 0xff8du : 0xff0du,
            0x10 => scan == 0x36 ? 0xffe2u : 0xffe1u,
            0x11 => extended ? 0xffe4u : 0xffe3u, 0x12 => extended ? 0xffeau : 0xffe9u,
            0xa0 => 0xffe1, 0xa1 => 0xffe2, 0xa2 => 0xffe3, 0xa3 => 0xffe4,
            0xa4 => 0xffe9, 0xa5 => 0xffea, 0x13 => 0xff13, 0x14 => 0xffe5,
            0x1b => 0xff1b, 0x20 => 32, 0x21 => 0xff55, 0x22 => 0xff56,
            0x23 => 0xff57, 0x24 => 0xff50, 0x25 => 0xff51, 0x26 => 0xff52,
            0x27 => 0xff53, 0x28 => 0xff54, 0x2c => 0xff61, 0x2d => 0xff63,
            0x2e => 0xffff, 0x6a => 0xffaa, 0x6b => 0xffab, 0x6d => 0xffad,
            0x6e => 0xffae, 0x6f => 0xffaf, 0x90 => 0xff7f, 0x91 => 0xff14,
            0xba => 59, 0xbb => 61, 0xbc => 44, 0xbd => 45, 0xbe => 46,
            0xbf => 47, 0xc0 => 96, 0xdb => 91, 0xdc => 92, 0xdd => 93, 0xde => 39,
            _ => 0
        };
    }
}

public readonly record struct GuestPointer(int X, int Y);
public static class DisplayCoordinates
{
    public static GuestPointer? Map(double x, double y, double viewWidth, double viewHeight, int guestWidth, int guestHeight, bool clamp = false)
    {
        if (guestWidth <= 0 || guestHeight <= 0 || viewWidth <= 0 || viewHeight <= 0) return null;
        double scale = Math.Min(viewWidth / guestWidth, viewHeight / guestHeight);
        double gx = (x - (viewWidth - guestWidth * scale) / 2) / scale;
        double gy = (y - (viewHeight - guestHeight * scale) / 2) / scale;
        if (!clamp && (gx < 0 || gy < 0 || gx >= guestWidth || gy >= guestHeight)) return null;
        return new(Math.Clamp((int)gx, 0, guestWidth - 1), Math.Clamp((int)gy, 0, guestHeight - 1));
    }
}
