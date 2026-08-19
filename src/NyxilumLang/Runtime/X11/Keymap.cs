using System.Collections.Generic;

namespace NyxilumLang.Runtime.X11;

// Портовано з rawgui/src/keymap.js - keycode -> keysym (СПРАВЖНЯ розкладка
// з сервера, GetKeyboardMapping) -> символ. Не хардкодиться жодна розкладка:
// друковані ASCII-символи в X11 мають keysym, що ЧИСЛОВО ДОРІВНЮЄ своєму
// коду символу (специфікація keysymdef.h: 0x20-0x7e = той самий код, що й
// ASCII/Unicode) - конвертація тривіальна. Спецклавіші (Enter, Backspace, ...)
// мають keysym-и у зарезервованому діапазоні 0xff00+.
public sealed class Keymap
{
    private static readonly Dictionary<uint, string> SpecialKeysyms = new()
    {
        [0xff08] = "BackSpace", [0xff09] = "Tab", [0xff0d] = "Return", [0xff1b] = "Escape",
        [0xffff] = "Delete", [0xff51] = "Left", [0xff52] = "Up", [0xff53] = "Right",
        [0xff54] = "Down", [0xff50] = "Home", [0xff57] = "End"
    };

    private readonly Dictionary<byte, uint[]> _map;

    private Keymap(Dictionary<byte, uint[]> map) { _map = map; }

    public static Keymap Load(X11Client client, X11Connection conn)
    {
        byte firstKeycode = conn.MinKeycode;
        int count = conn.MaxKeycode - conn.MinKeycode + 1;
        var reply = client.Request(X11Protocol.BuildGetKeyboardMapping(firstKeycode, (byte)count));
        var map = X11Protocol.ParseGetKeyboardMappingReply(reply, firstKeycode, count);
        return new Keymap(map);
    }

    public uint[] KeycodeToKeysyms(byte keycode) => _map.TryGetValue(keycode, out var k) ? k : System.Array.Empty<uint>();

    // shiftPressed вибирає між keysyms[0] (без Shift) і keysyms[1] (з Shift) -
    // стандартна конвенція X11 для "групи 1"; CapsLock і додаткові групи
    // (AltGr тощо) свідомо не підтримуються в цій першій версії.
    public string? KeycodeToChar(byte keycode, bool shiftPressed)
    {
        var keysyms = KeycodeToKeysyms(keycode);
        uint keysym = (shiftPressed && keysyms.Length > 1 && keysyms[1] != 0) ? keysyms[1] : (keysyms.Length > 0 ? keysyms[0] : 0);
        return KeysymToChar(keysym);
    }

    public static string? KeysymToChar(uint keysym)
    {
        if (keysym == 0) return null;
        if (keysym >= 0x20 && keysym <= 0x7e) return ((char)keysym).ToString();
        return SpecialKeysyms.TryGetValue(keysym, out var name) ? name : null;
    }
}
