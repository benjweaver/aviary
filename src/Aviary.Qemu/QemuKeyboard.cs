namespace Aviary.Qemu;

// Maps text and key-combo names to QEMU qcodes for QMP send-key. Assumes a US keyboard layout in the guest.
public static class QemuKeyboard
{
    static readonly Dictionary<char, string> Plain = new()
    {
        [' '] = "spc", ['-'] = "minus", ['='] = "equal", ['['] = "bracket_left", [']'] = "bracket_right", ['\\'] = "backslash",
        [';'] = "semicolon", ['\''] = "apostrophe", [','] = "comma", ['.'] = "dot", ['/'] = "slash", ['`'] = "grave_accent",
        ['\n'] = "ret", ['\t'] = "tab",
    };
    static readonly Dictionary<char, string> Shifted = new()
    {
        ['!'] = "1", ['@'] = "2", ['#'] = "3", ['$'] = "4", ['%'] = "5", ['^'] = "6", ['&'] = "7", ['*'] = "8", ['('] = "9", [')'] = "0",
        ['_'] = "minus", ['+'] = "equal", ['{'] = "bracket_left", ['}'] = "bracket_right", ['|'] = "backslash", [':'] = "semicolon",
        ['"'] = "apostrophe", ['<'] = "comma", ['>'] = "dot", ['?'] = "slash", ['~'] = "grave_accent",
    };
    static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = "ctrl", ["control"] = "ctrl", ["alt"] = "alt", ["shift"] = "shift", ["win"] = "meta_l", ["super"] = "meta_l", ["meta"] = "meta_l", ["cmd"] = "meta_l",
        ["enter"] = "ret", ["return"] = "ret", ["esc"] = "esc", ["escape"] = "esc", ["tab"] = "tab", ["space"] = "spc", ["backspace"] = "backspace",
        ["delete"] = "delete", ["del"] = "delete", ["insert"] = "insert", ["home"] = "home", ["end"] = "end", ["pageup"] = "pgup", ["pagedown"] = "pgdn",
        ["up"] = "up", ["down"] = "down", ["left"] = "left", ["right"] = "right", ["menu"] = "menu", ["printscreen"] = "print",
    };

    // One entry per keystroke; each is a chord of qcodes pressed together.
    public static IEnumerable<string[]> Text(string text)
    {
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            if (Plain.TryGetValue(c, out var plain)) yield return [plain];
            else if (Shifted.TryGetValue(c, out var shifted)) yield return ["shift", shifted];
            else if (c is >= 'A' and <= 'Z') yield return ["shift", char.ToLowerInvariant(c).ToString()];
            else if (c is >= 'a' and <= 'z' or >= '0' and <= '9') yield return [c.ToString()];
            else throw new ArgumentException($"Cannot type '{c}' (U+{(int)c:X4}) with a US keyboard layout.", nameof(text));
        }
    }

    // "ctrl+alt+t", "enter", "f5", "a".
    public static string[] Chord(string combo)
    {
        var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keys.Length == 0) throw new ArgumentException("Empty key combination.", nameof(combo));
        return keys.Select(key =>
        {
            if (Named.TryGetValue(key, out var named)) return named;
            if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return char.ToLowerInvariant(key[0]).ToString();
            if (key.Length is 2 or 3 && (key[0] is 'f' or 'F') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 12) return "f" + f;
            if (key.Length == 1 && Plain.TryGetValue(key[0], out var symbol)) return symbol;
            throw new ArgumentException($"Unknown key '{key}'.", nameof(combo));
        }).ToArray();
    }
}
