using System.IO.Compression;

namespace NyxilumLang.Runtime.Canvas;

// Растровий шрифт формату PSF1 (консольні шрифти Linux, Terminus Font з
// console-setup) - вшитий у збірку ресурсом (NyxilumLang.Fonts.*.psf.gz).
// На відміну від X11/Font5x7 (лише ВЕЛИКІ літери, 5x7), тут повний набір:
// латиниця, кирилиця з ґ/є/і/ї, псевдографіка, стрілки - і кілька розмірів
// (12..32 px заввишки), звичайний і жирний.
//
// PSF1: [0x36 0x04][mode][charsize] + гліфи (256 або 512, кожен charsize
// байтів - по байту на рядок, ширина завжди 8) + таблиця Unicode (mode & 2):
// для кожного гліфа - послідовність u16 кодів, 0xFFFF завершує запис гліфа.
//
// PSF2 (магія 72 b5 4a 86) - для ширших шрифтів (10x20, 12x24, ...):
// заголовок з width/height/bytesPerGlyph, рядок = ceil(width/8) байтів, таблиця
// Unicode - UTF-8 послідовності, 0xFF завершує запис гліфа.
public sealed class PsfFont
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    private int _rowBytes;
    private int _bytesPerGlyph;
    private byte[] _glyphs = Array.Empty<byte>();
    private readonly Dictionary<int, int> _map = new();
    private int _fallback;

    public static PsfFont Load(byte[] data)
    {
        var f = new PsfFont();
        if (data.Length > 4 && data[0] == 0x36 && data[1] == 0x04)
        {
            int mode = data[2];
            int count = (mode & 1) != 0 ? 512 : 256;
            f.Width = 8; f.Height = data[3]; f._rowBytes = 1; f._bytesPerGlyph = data[3];
            f._glyphs = data.AsSpan(4, count * f._bytesPerGlyph).ToArray();
            if ((mode & 2) != 0)
            {
                int pos = 4 + count * f._bytesPerGlyph;
                for (int g = 0; g < count && pos + 1 < data.Length; g++)
                {
                    while (pos + 1 < data.Length)
                    {
                        int u = data[pos] | (data[pos + 1] << 8);
                        pos += 2;
                        if (u == 0xFFFF) break;
                        if (u != 0xFFFE) f._map.TryAdd(u, g);
                    }
                }
            }
            else for (int g = 0; g < count; g++) f._map[g] = g;
        }
        else if (data.Length > 32 && data[0] == 0x72 && data[1] == 0xb5 && data[2] == 0x4a && data[3] == 0x86)
        {
            int U32(int at) => BitConverter.ToInt32(data, at);
            int headerSize = U32(8), flags = U32(12), count = U32(16);
            f._bytesPerGlyph = U32(20); f.Height = U32(24); f.Width = U32(28);
            f._rowBytes = (f.Width + 7) / 8;
            f._glyphs = data.AsSpan(headerSize, count * f._bytesPerGlyph).ToArray();
            if ((flags & 1) != 0)
            {
                int pos = headerSize + count * f._bytesPerGlyph;
                for (int g = 0; g < count && pos < data.Length; g++)
                {
                    while (pos < data.Length && data[pos] != 0xFF)
                    {
                        if (data[pos] == 0xFE) { pos++; continue; }
                        int len = data[pos] < 0x80 ? 1 : data[pos] < 0xE0 ? 2 : data[pos] < 0xF0 ? 3 : 4;
                        if (pos + len > data.Length) { pos = data.Length; break; }
                        var s = System.Text.Encoding.UTF8.GetString(data, pos, len);
                        if (s.Length > 0) f._map.TryAdd(char.ConvertToUtf32(s, 0), g);
                        pos += len;
                    }
                    pos++;
                }
            }
            else for (int g = 0; g < count; g++) f._map[g] = g;
        }
        else throw new Exception("Невідомий формат шрифту (очікувався PSF1/PSF2)");

        f._fallback = f._map.TryGetValue('?', out var q) ? q : 0;
        return f;
    }

    public bool HasGlyph(int codepoint) => _map.ContainsKey(codepoint);

    // Біт (x, y) гліфа для символу codepoint. Невідомий символ -> '?'.
    public int GlyphIndex(int codepoint) => _map.TryGetValue(codepoint, out var g) ? g : _fallback;

    // Увімкнені пікселі гліфа як готовий список (y * Width + x) - DrawText
    // не перебирає всі біти щоразу. Кеш ліниво по гліфах.
    private int[]?[]? _onPixels;
    public int[] OnPixels(int glyph)
    {
        _onPixels ??= new int[]?[_glyphs.Length / _bytesPerGlyph];
        var cached = _onPixels[glyph];
        if (cached != null) return cached;
        var list = new List<int>();
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (Pixel(glyph, x, y)) list.Add(y * Width + x);
        return _onPixels[glyph] = list.ToArray();
    }

    public bool Pixel(int glyph, int x, int y)
    {
        int b = _glyphs[glyph * _bytesPerGlyph + y * _rowBytes + (x >> 3)];
        return (b & (0x80 >> (x & 7))) != 0;
    }

    // ---- Набір вшитих шрифтів ----

    private static readonly (string File, int Height)[] Regular =
    {
        ("Uni2-Terminus12x6", 12), ("Uni2-Terminus14", 14), ("Uni2-Terminus16", 16), ("Uni2-Terminus20x10", 20),
        ("Uni2-Terminus24x12", 24), ("Uni2-Terminus28x14", 28), ("Uni2-Terminus32x16", 32)
    };
    private static readonly (string File, int Height)[] Bold =
    {
        ("Uni2-TerminusBold14", 14), ("Uni2-TerminusBold16", 16), ("Uni2-TerminusBold20x10", 20),
        ("Uni2-TerminusBold24x12", 24), ("Uni2-TerminusBold28x14", 28), ("Uni2-TerminusBold32x16", 32)
    };

    private static readonly Dictionary<string, PsfFont> _cache = new();
    private static readonly object _lock = new();

    // Найбільший вшитий шрифт, не вищий за size (або найменший, якщо size
    // менший за всі). Розміри понад 32 - той самий 32 px, збільшений цілим
    // множником (scale) - див. SoftCanvas.DrawText.
    public static (PsfFont Font, int Scale) ForSize(int size, bool bold)
    {
        var table = bold ? Bold : Regular;
        int scale = 1;
        if (size > 32) { scale = Math.Max(1, (int)Math.Round(size / 32.0)); size = (int)Math.Round((double)size / scale); }
        var pick = table[0];
        foreach (var t in table) if (t.Height <= size) pick = t;
        return (Get(pick.File), scale);
    }

    private static PsfFont Get(string file)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(file, out var cached)) return cached;
            var asm = typeof(PsfFont).Assembly;
            using var res = asm.GetManifestResourceStream($"NyxilumLang.Fonts.{file}.psf.gz")
                ?? throw new Exception($"Шрифт {file} не вшито у збірку");
            using var gz = new GZipStream(res, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            var font = Load(ms.ToArray());
            _cache[file] = font;
            return font;
        }
    }
}
