namespace Aviary.HyperV;

// Steps for Msvm_Keyboard: plain text goes through TypeText (ASCII); chords are Windows virtual-key codes.
public static class HyperVKeyboard
{
    public sealed record Step(string? Text, int[]? Keys);

    const int Enter = 0x0D;
    static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10, ["win"] = 0x5B, ["super"] = 0x5B, ["meta"] = 0x5B, ["cmd"] = 0x5B,
        ["enter"] = Enter, ["return"] = Enter, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20, ["backspace"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28, ["menu"] = 0x5D, ["printscreen"] = 0x2C,
    };

    public static IEnumerable<Step> Text(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Any(c => c is < ' ' or > '~' && c != '\t')) throw new ArgumentException("Hyper-V can only type printable ASCII text.", nameof(text));
            if (lines[i].Length > 0) yield return new(lines[i], null);
            if (i < lines.Length - 1) yield return new(null, [Enter]);
        }
    }

    public static Step Chord(string combo)
    {
        var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keys.Length == 0) throw new ArgumentException("Empty key combination.", nameof(combo));
        return new(null, keys.Select(key =>
        {
            if (Named.TryGetValue(key, out var code)) return code;
            if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return (int)char.ToUpperInvariant(key[0]);
            if (key.Length is 2 or 3 && (key[0] is 'f' or 'F') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 12) return 0x6F + f;
            throw new ArgumentException($"Unknown key '{key}'.", nameof(combo));
        }).ToArray());
    }
}
