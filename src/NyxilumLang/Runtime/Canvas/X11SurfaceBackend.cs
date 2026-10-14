#if !WINDOWS
using NyxilumLang.Runtime.X11;

namespace NyxilumLang.Runtime.Canvas;

// Linux/Mac: вікно X11Window (власний протокол, Runtime/X11/), пікселі
// SoftCanvas вантажаться в його backbuffer запитами PutImage (смугами - один
// запит обмежений MaxRequestLength) і копіюються на екран (Repaint).
public sealed class X11SurfaceBackend : ISurfaceBackend
{
    private readonly X11Window _win;
    private readonly Surface _surface;
    private volatile Keymap _keymap;
    private static readonly bool KeyDebug = Environment.GetEnvironmentVariable("NX_KEYDEBUG") == "1";
    private readonly int _bpp;

    public X11SurfaceBackend(Surface surface, string title, int width, int height)
    {
        _surface = surface;
        _win = X11Window.Create(width: width, height: height, title: title, background: 0x000000,
            extraEventMask: X11Protocol.EventMask.PointerMotion | X11Protocol.EventMask.KeyRelease);
        _win.MarkHasDeleteRequestListener();
        bool xkb = _win.Client.EnableXkb();
        if (KeyDebug) Console.Error.WriteLine($"[key] XKB: {(xkb ? "увімкнено" : "недоступне")}");
        _keymap = Keymap.Load(_win.Client, _win.Client.Conn);
        var conn = _win.Client.Conn;
        _bpp = conn.PixmapFormats.TryGetValue(_win.Screen.RootDepth, out var b) ? b : 32;
        if (_bpp != 32 && _bpp != 24)
            throw new Exception($"surfaceOpen: X-сервер має {_bpp} біт на піксель - підтримуються лише 24/32");

        _win.OnExpose += () => _win.Repaint();
        _win.OnResize += (w, h) =>
        {
            _forceFull = true;
            var e = Surface.MakeEvent("resize");
            e["width"] = (double)w; e["height"] = (double)h;
            _surface.Post(e);
        };
        _win.OnMouseDown += evt => PostButton(evt, true);
        _win.OnMouseUp += evt => PostButton(evt, false);
        _win.OnMotion += evt =>
        {
            var e = Surface.MakeEvent("mousemove");
            AddMouse(e, evt);
            _surface.Post(e);
        };
        // GNOME/Wayland перемикає розкладку для X11-клієнтів, ПІДМІНЯЮЧИ карту
        // клавіш (сервер шле MappingNotify) - стару карту треба перечитати.
        // Request з потоку читання сокета заблокував би сам себе (відповідь
        // читає той самий потік) - тому окремим завданням.
        _win.Client.OnEvent += evt =>
        {
            if (evt.Name != "MappingNotify") return;
            if (KeyDebug) Console.Error.WriteLine("[key] MappingNotify - перечитую карту клавіш");
            Task.Run(() => { try { _keymap = Keymap.Load(_win.Client, _win.Client.Conn); } catch { } });
        };
        _win.OnKey += evt => PostKey(evt, true);
        _win.OnKeyRelease += evt => PostKey(evt, false);
        _win.OnDeleteRequest += () => _surface.Post(Surface.MakeEvent("close"));
        _win.OnClose += () => _surface.Post(Surface.MakeEvent("close"));
    }

    private static void AddMouse(Dictionary<string, object> e, X11Event evt)
    {
        e["x"] = (double)evt.EventX;
        e["y"] = (double)evt.EventY;
        AddMods(e, evt.State);
    }

    private static void AddMods(Dictionary<string, object> e, ushort state)
    {
        e["shift"] = (state & 0x1) != 0;
        e["ctrl"] = (state & 0x4) != 0;
        e["alt"] = (state & 0x8) != 0;
    }

    private void PostButton(X11Event evt, bool down)
    {
        // Кнопки 4/5 (6/7) - колесо вертикальне (горизонтальне): X11 шле їх
        // як натискання+відпускання; беремо лише натискання.
        if (evt.Detail >= 4 && evt.Detail <= 7)
        {
            if (!down) return;
            var w = Surface.MakeEvent("wheel");
            AddMouse(w, evt);
            w["dy"] = evt.Detail == 4 ? -1.0 : evt.Detail == 5 ? 1.0 : 0.0;
            w["dx"] = evt.Detail == 6 ? -1.0 : evt.Detail == 7 ? 1.0 : 0.0;
            _surface.Post(w);
            return;
        }
        var e = Surface.MakeEvent(down ? "mousedown" : "mouseup");
        AddMouse(e, evt);
        e["button"] = (double)evt.Detail;
        _surface.Post(e);
    }

    private void PostKey(X11Event evt, bool down)
    {
        var syms = _keymap.KeycodeToKeysyms(evt.Detail);
        if (KeyDebug && down)
            Console.Error.WriteLine($"[key] keycode={evt.Detail} state=0x{evt.State:x} група={(evt.State >> 13) & 3} keysyms=[{string.Join(" ", syms.Select(k => "0x" + k.ToString("x")))}]");
        if (syms.Length == 0) return;
        bool shift = (evt.State & 0x1) != 0, caps = (evt.State & 0x2) != 0;
        bool ctrl = (evt.State & 0x4) != 0, alt = (evt.State & 0x8) != 0;

        // Назва клавіші - з ПЕРШОЇ групи (латинська розкладка) без Shift.
        uint baseSym = syms[0];
        string? key = KeyName(baseSym, syms.Length > 1 ? syms[1] : 0);
        if (key == null) return;

        var e = Surface.MakeEvent(down ? "key" : "keyup");
        e["key"] = key;
        AddMods(e, evt.State);
        _surface.Post(e);
        if (!down || ctrl || alt) return;

        // Набраний символ - з ПОТОЧНОЇ групи розкладки (біти 13-14 стану,
        // XKB): у core-мапі keysym-и йдуть [група1, група1+Shift, група2,
        // група2+Shift, ...].
        int group = (evt.State >> 13) & 3;
        uint lower = Sym(syms, group * 2), upper = Sym(syms, group * 2 + 1);
        // AltGr (ISO_Level3_Shift, mod5 = 0x80) - рівні 3/4 клавіші. Core-мапа
        // XKB: [г1р1, г1р2, г2р1, г2р2, далі зайві рівні груп по черзі]; коли
        // перша група (us) має лише 2 рівні, рівні 3/4 другої групи стоять на
        // індексах 4/5 (перевірено на us+ua під XWayland). Точний розклад для
        // будь-яких розкладок - лише через XkbGetMap; поки - цей випадок.
        if ((evt.State & 0x80) != 0 && group == 1)
        {
            uint l3 = Sym(syms, 4), l4 = Sym(syms, 5);
            if (l3 != 0) { lower = l3; upper = l4; }
        }
        if (lower == 0) { lower = syms[0]; upper = Sym(syms, 1); }
        int cpLower = KeysymToCodepoint(lower), cpUpper = upper != 0 ? KeysymToCodepoint(upper) : -1;
        bool useUpper = shift;
        // CapsLock діє лише на літери (не на цифри/знаки).
        if (caps && cpLower >= 0 && char.IsLetter((char)cpLower)) useUpper = !useUpper;
        int cp = useUpper && cpUpper >= 0 ? cpUpper : cpLower;
        if (useUpper && cpUpper < 0 && cpLower >= 0) cp = char.ToUpperInvariant((char)cpLower);
        if (cp < 0x20 || cp == 0x7f) return;

        var t = Surface.MakeEvent("text");
        t["text"] = char.ConvertFromUtf32(cp);
        _surface.Post(t);
    }

    private static uint Sym(uint[] syms, int i) => i < syms.Length ? syms[i] : 0;

    private static readonly Dictionary<uint, string> SpecialKeys = new()
    {
        [0xff08] = "Backspace", [0xff09] = "Tab", [0xfe20] = "Tab", [0xff0d] = "Enter", [0xff8d] = "Enter",
        [0xff1b] = "Escape", [0xffff] = "Delete", [0xff9f] = "Delete", [0xff63] = "Insert",
        [0xff51] = "Left", [0xff52] = "Up", [0xff53] = "Right", [0xff54] = "Down",
        [0xff96] = "Left", [0xff97] = "Up", [0xff98] = "Right", [0xff99] = "Down",
        [0xff50] = "Home", [0xff57] = "End", [0xff95] = "Home", [0xff9c] = "End",
        [0xff55] = "PageUp", [0xff56] = "PageDown", [0xff9a] = "PageUp", [0xff9b] = "PageDown",
        [0xffe1] = "Shift", [0xffe2] = "Shift", [0xffe3] = "Control", [0xffe4] = "Control",
        [0xffe9] = "Alt", [0xffea] = "Alt", [0xfe03] = "Alt", [0xffeb] = "Super", [0xffec] = "Super",
        [0xffe5] = "CapsLock", [0x20] = "Space",
    };

    private static string? KeyName(uint sym, uint shifted)
    {
        if (SpecialKeys.TryGetValue(sym, out var n)) return n;
        if (sym >= 0xffbe && sym <= 0xffc9) return "F" + (sym - 0xffbe + 1);
        if (sym >= 0xffb0 && sym <= 0xffb9) return ((char)('0' + sym - 0xffb0)).ToString();
        int cp = KeysymToCodepoint(sym);
        if (cp < 0x20) return null;
        return char.ConvertFromUtf32(cp).ToLowerInvariant();
    }

    // Кирилиця keysymdef.h (0x6c0..0x6ff): малі літери в порядку KOI8.
    private const string CyrKoi = "юабцдефгхийклмнопярстужвьызшэщчъ";

    public static int KeysymToCodepoint(uint sym)
    {
        if (sym >= 0x20 && sym <= 0x7e) return (int)sym;
        if (sym >= 0xa0 && sym <= 0xff) return (int)sym;
        if (sym >= 0x01000000 && sym <= 0x0110ffff) return (int)(sym - 0x01000000);
        if (sym >= 0xffb0 && sym <= 0xffb9) return '0' + (int)(sym - 0xffb0);
        if (sym >= 0x6c0 && sym <= 0x6df) return CyrKoi[(int)(sym - 0x6c0)];
        if (sym >= 0x6e0 && sym <= 0x6ff) return char.ToUpperInvariant(CyrKoi[(int)(sym - 0x6e0)]);
        return sym switch
        {
            0x6a4 => 'є', 0x6a6 => 'і', 0x6a7 => 'ї', 0x6ad => 'ґ', 0x6a3 => 'ё',
            0x6b4 => 'Є', 0x6b6 => 'І', 0x6b7 => 'Ї', 0x6bd => 'Ґ', 0x6b3 => 'Ё',
            0x6b0 => '№', 0x20ac => '€',
            0xffaa => '*', 0xffab => '+', 0xffad => '-', 0xffae => '.', 0xffaf => '/',
            _ => -1
        };
    }

    private byte[] _row = Array.Empty<byte>();
    // Копія останнього показаного кадру: шлемо лише рядки, що змінилися
    // (друк у редакторі міняє кілька рядків пікселів, а не всі 800) -
    // повний кадр 1280x800 це 4 МБ через сокет, ~25 мс.
    private uint[] _last = Array.Empty<uint>();
    private int _lastW, _lastH;
    private volatile bool _forceFull = true;

    public void Present(SoftCanvas c)
    {
        int w = Math.Min(c.Width, _win.Width), h = Math.Min(c.Height, _win.Height);
        if (w <= 0 || h <= 0) return;
        bool full = _forceFull || _lastW != c.Width || _lastH != c.Height || _last.Length != c.Pixels.Length;
        _forceFull = false;

        int bytesPerPixel = _bpp / 8;
        int stride = w * bytesPerPixel; stride += X11Protocol.Pad(stride);
        int maxBytes = Math.Max(stride, _win.Client.Conn.MaxRequestLength * 4 - 64);
        int rowsPerStrip = Math.Max(1, maxBytes / stride);
        if (_row.Length < stride * rowsPerStrip) _row = new byte[stride * rowsPerStrip];
        bool msb = _win.Client.Conn.ImageByteOrder == 1;
        bool sent = false;

        int y = 0;
        while (y < h)
        {
            if (!full && c.Pixels.AsSpan(y * c.Width, w).SequenceEqual(_last.AsSpan(y * c.Width, w))) { y++; continue; }
            int y0 = y, rows = 0;
            while (y < h && rows < rowsPerStrip &&
                   (full || !c.Pixels.AsSpan(y * c.Width, w).SequenceEqual(_last.AsSpan(y * c.Width, w))))
            { y++; rows++; }
            for (int r = 0; r < rows; r++)
            {
                int src = (y0 + r) * c.Width, dst = r * stride;
                for (int x = 0; x < w; x++)
                {
                    uint p = c.Pixels[src + x];
                    if (bytesPerPixel == 4)
                    {
                        if (msb) { _row[dst] = 0; _row[dst + 1] = (byte)(p >> 16); _row[dst + 2] = (byte)(p >> 8); _row[dst + 3] = (byte)p; }
                        else { _row[dst] = (byte)p; _row[dst + 1] = (byte)(p >> 8); _row[dst + 2] = (byte)(p >> 16); _row[dst + 3] = 0; }
                    }
                    else { _row[dst] = (byte)p; _row[dst + 1] = (byte)(p >> 8); _row[dst + 2] = (byte)(p >> 16); }
                    dst += bytesPerPixel;
                }
            }
            _win.Client.Send(X11Protocol.BuildPutImage(_win.Pixmap, _win.Gc, (ushort)w, (ushort)rows, 0, (short)y0,
                _win.Screen.RootDepth, _row.AsSpan(0, stride * rows)));
            sent = true;
        }

        if (_last.Length != c.Pixels.Length) _last = new uint[c.Pixels.Length];
        c.Pixels.CopyTo(_last, 0);
        _lastW = c.Width; _lastH = c.Height;
        if (sent) _win.Repaint();
    }

    public void SetTitle(string title) => _win.SetTitle(title);
    public void Close() { try { _win.Close(); } catch { } }
    public void Pump() { }
}
#endif
