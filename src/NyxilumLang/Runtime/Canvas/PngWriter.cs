using System.IO.Compression;

namespace NyxilumLang.Runtime.Canvas;

// Мінімальний запис PNG (RGB 8 біт, без фільтрів) - лише .NET (ZLibStream),
// без System.Drawing, тож однаково на всіх платформах. Для surfaceSave:
// скриншоти програм і автоматичні перевірки IDE.
public static class PngWriter
{
    public static void Save(SoftCanvas c, string path)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + c.Width * 3];
            for (int y = 0; y < c.Height; y++)
            {
                row[0] = 0;
                for (int x = 0; x < c.Width; x++)
                {
                    uint p = c.Pixels[y * c.Width + x];
                    row[1 + x * 3] = (byte)(p >> 16); row[2 + x * 3] = (byte)(p >> 8); row[3 + x * 3] = (byte)p;
                }
                z.Write(row);
            }
        }
        using var f = File.Create(path);
        f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)c.Width); WriteBE(ihdr, 4, (uint)c.Height);
        ihdr[8] = 8; ihdr[9] = 2; // 8 біт, RGB
        Chunk(f, "IHDR", ihdr);
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", Array.Empty<byte>());
    }

    private static void WriteBE(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, (uint)data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        uint crc = Crc(t, 0xffffffff); crc = Crc(data, crc) ^ 0xffffffff;
        var cb = new byte[4]; WriteBE(cb, 0, crc); s.Write(cb);
    }

    private static uint[]? _table;
    private static uint Crc(byte[] data, uint crc)
    {
        if (_table == null)
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1; t[n] = c; }
            _table = t;
        }
        foreach (var b in data) crc = _table[(crc ^ b) & 0xff] ^ (crc >> 8);
        return crc;
    }
}
