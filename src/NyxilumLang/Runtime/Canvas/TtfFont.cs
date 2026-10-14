using System.IO.Compression;

namespace NyxilumLang.Runtime.Canvas;

// TrueType-шрифти з власним розбором і растеризацією - без зовнішніх
// бібліотек (System.Drawing є лише на Windows), тож текст однаковий на
// Linux і Windows.
//
// Розбір: таблиці head/maxp/hhea/hmtx/cmap (формат 4 і 12)/loca/glyf
// (прості й складені гліфи). Растеризація зі згладжуванням: контури
// (квадратичні криві розбиваються на відрізки) накопичують "площу й
// покриття" в рядках буфера, потім префіксна сума дає точну частку
// покриття кожного пікселя (той самий підхід, що font-rs / stb_truetype).
// Гліф для пари (гліф, розмір) рахується один раз і кешується.
//
// Вбудовано Noto Sans і Noto Sans Mono (OFL-1.1, Fonts/LICENSE-NotoFonts.txt):
// "sans" - інтерфейс, "mono" - код.
public sealed class TtfFont
{
    private readonly byte[] _d;
    public int UnitsPerEm { get; private set; }
    public int Ascender { get; private set; }
    public int Descender { get; private set; }
    private int _locFmt, _numGlyphs, _numHMetrics, _glyf, _loca, _hmtx;
    private readonly Dictionary<int, int> _cmap = new();

    private TtfFont(byte[] data) { _d = data; }

    private int U8(int o) => _d[o];
    private int U16(int o) => _d[o] << 8 | _d[o + 1];
    private int S16(int o) => (short)(_d[o] << 8 | _d[o + 1]);
    private int U32(int o) => (int)((uint)_d[o] << 24 | (uint)_d[o + 1] << 16 | (uint)_d[o + 2] << 8 | _d[o + 3]);

    public static TtfFont Load(byte[] data)
    {
        var f = new TtfFont(data);
        int numTables = f.U16(4);
        var tables = new Dictionary<string, int>();
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            string tag = System.Text.Encoding.ASCII.GetString(data, rec, 4);
            tables[tag] = f.U32(rec + 8);
        }
        int head = tables["head"], hhea = tables["hhea"];
        f.UnitsPerEm = f.U16(head + 18);
        f._locFmt = f.S16(head + 50);
        f._numGlyphs = f.U16(tables["maxp"] + 4);
        f.Ascender = f.S16(hhea + 4);
        f.Descender = f.S16(hhea + 6);
        f._numHMetrics = f.U16(hhea + 34);
        f._hmtx = tables["hmtx"];
        f._loca = tables["loca"];
        f._glyf = tables["glyf"];
        f.ReadCmap(tables["cmap"]);
        return f;
    }

    private void ReadCmap(int cmap)
    {
        int n = U16(cmap + 2);
        int fmt4 = -1, fmt12 = -1;
        for (int i = 0; i < n; i++)
        {
            int rec = cmap + 4 + i * 8;
            int platform = U16(rec), enc = U16(rec + 2);
            int sub = cmap + U32(rec + 4);
            int fmt = U16(sub);
            if (fmt == 12 && (platform == 3 && enc == 10 || platform == 0)) fmt12 = sub;
            if (fmt == 4 && (platform == 3 && enc == 1 || platform == 0)) fmt4 = sub;
        }
        if (fmt12 >= 0)
        {
            int groups = U32(fmt12 + 12);
            for (int g = 0; g < groups; g++)
            {
                int o = fmt12 + 16 + g * 12;
                int start = U32(o), end = U32(o + 4), glyph = U32(o + 8);
                if (end - start > 0x30000) continue;
                for (int c = start; c <= end; c++) _cmap[c] = glyph + (c - start);
            }
            return;
        }
        if (fmt4 < 0) return;
        int segX2 = U16(fmt4 + 6);
        int ends = fmt4 + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
        for (int s = 0; s < segX2 / 2; s++)
        {
            int end = U16(ends + s * 2), start = U16(starts + s * 2);
            int delta = S16(deltas + s * 2), rangeOff = U16(ranges + s * 2);
            for (int c = start; c <= end && c != 0xffff; c++)
            {
                int g;
                if (rangeOff == 0) g = (c + delta) & 0xffff;
                else
                {
                    int addr = ranges + s * 2 + rangeOff + 2 * (c - start);
                    g = U16(addr);
                    if (g != 0) g = (g + delta) & 0xffff;
                }
                if (g != 0) _cmap[c] = g;
            }
        }
    }

    public int GlyphIndex(int codepoint) => _cmap.TryGetValue(codepoint, out var g) ? g : 0;
    public bool HasGlyph(int codepoint) => _cmap.ContainsKey(codepoint);

    public int AdvanceUnits(int glyph)
    {
        int i = Math.Min(glyph, _numHMetrics - 1);
        return U16(_hmtx + i * 4);
    }

    private (int off, int len) GlyphRange(int g)
    {
        if (g < 0 || g >= _numGlyphs) return (0, 0);
        int a, b;
        if (_locFmt == 0) { a = U16(_loca + g * 2) * 2; b = U16(_loca + g * 2 + 2) * 2; }
        else { a = U32(_loca + g * 4); b = U32(_loca + g * 4 + 4); }
        return (_glyf + a, b - a);
    }

    // Контури гліфа в одиницях шрифту: кожен - список (x, y, наКривій).
    private void Outline(int g, List<List<(float x, float y, bool on)>> outC, float a, float b, float c, float d, float dx, float dy, int depth)
    {
        var (o, len) = GlyphRange(g);
        if (len <= 0 || depth > 8) return;
        int nc = S16(o);
        if (nc >= 0)
        {
            int p = o + 10;
            var endPts = new int[nc];
            for (int i = 0; i < nc; i++) endPts[i] = U16(p + i * 2);
            p += nc * 2;
            int nPts = nc == 0 ? 0 : endPts[nc - 1] + 1;
            p += 2 + U16(p);
            var flags = new byte[nPts];
            for (int i = 0; i < nPts;)
            {
                byte fl = _d[p++];
                flags[i++] = fl;
                if ((fl & 8) != 0)
                {
                    int rep = _d[p++];
                    while (rep-- > 0 && i < nPts) flags[i++] = fl;
                }
            }
            var xs = new int[nPts];
            int v = 0;
            for (int i = 0; i < nPts; i++)
            {
                byte fl = flags[i];
                if ((fl & 2) != 0) { int dv = _d[p++]; v += (fl & 16) != 0 ? dv : -dv; }
                else if ((fl & 16) == 0) { v += S16(p); p += 2; }
                xs[i] = v;
            }
            v = 0;
            var ys = new int[nPts];
            for (int i = 0; i < nPts; i++)
            {
                byte fl = flags[i];
                if ((fl & 4) != 0) { int dv = _d[p++]; v += (fl & 32) != 0 ? dv : -dv; }
                else if ((fl & 32) == 0) { v += S16(p); p += 2; }
                ys[i] = v;
            }
            int start = 0;
            for (int ci = 0; ci < nc; ci++)
            {
                var pts = new List<(float, float, bool)>();
                for (int i = start; i <= endPts[ci]; i++)
                    pts.Add((a * xs[i] + c * ys[i] + dx, b * xs[i] + d * ys[i] + dy, (flags[i] & 1) != 0));
                if (pts.Count > 0) outC.Add(pts);
                start = endPts[ci] + 1;
            }
            return;
        }
        // складений гліф: частини з іншими гліфами, зсувом і масштабом
        int q = o + 10;
        while (true)
        {
            int fl = U16(q), sub = U16(q + 2);
            q += 4;
            float ox, oy;
            if ((fl & 1) != 0) { ox = S16(q); oy = S16(q + 2); q += 4; }
            else { ox = (sbyte)_d[q]; oy = (sbyte)_d[q + 1]; q += 2; }
            if ((fl & 2) == 0) { ox = 0; oy = 0; } // прив'язка за точками - рідко, ігноруємо
            float ma = 1, mb = 0, mc = 0, md = 1;
            if ((fl & 8) != 0) { ma = md = S16(q) / 16384f; q += 2; }
            else if ((fl & 0x40) != 0) { ma = S16(q) / 16384f; md = S16(q + 2) / 16384f; q += 4; }
            else if ((fl & 0x80) != 0) { ma = S16(q) / 16384f; mb = S16(q + 2) / 16384f; mc = S16(q + 4) / 16384f; md = S16(q + 6) / 16384f; q += 8; }
            // композиція: спершу матриця частини, потім батьківська
            float na = a * ma + c * mb, nb = b * ma + d * mb, nc2 = a * mc + c * md, nd = b * mc + d * md;
            float ndx = a * ox + c * oy + dx, ndy = b * ox + d * oy + dy;
            Outline(sub, outC, na, nb, nc2, nd, ndx, ndy, depth + 1);
            if ((fl & 0x20) == 0) break;
        }
    }

    // ---------- растеризація ----------

    public sealed class Glyph
    {
        public byte[] Alpha = Array.Empty<byte>();
        public int W, H, Left, Top; // Left/Top - від точки пера на базовій лінії
        public float Advance;
    }

    private readonly Dictionary<long, Glyph> _cache = new();

    public Glyph Render(int g, float px)
    {
        long key = (long)g << 20 | (long)(px * 16);
        lock (_cache)
            if (_cache.TryGetValue(key, out var hit)) return hit;
        var gl = RenderUncached(g, px);
        lock (_cache) _cache[key] = gl;
        return gl;
    }

    private Glyph RenderUncached(int g, float px)
    {
        float s = px / UnitsPerEm;
        var gl = new Glyph { Advance = AdvanceUnits(g) * s };
        var contours = new List<List<(float x, float y, bool on)>>();
        Outline(g, contours, 1, 0, 0, 1, 0, 0, 0);
        if (contours.Count == 0) return gl;
        // у пікселі (y донизу)
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var ct in contours)
            foreach (var (x, y, _) in ct)
            {
                minX = Math.Min(minX, x * s); maxX = Math.Max(maxX, x * s);
                minY = Math.Min(minY, -y * s); maxY = Math.Max(maxY, -y * s);
            }
        int left = (int)Math.Floor(minX), top = (int)Math.Floor(minY);
        int w = (int)Math.Ceiling(maxX) - left + 1, h = (int)Math.Ceiling(maxY) - top + 1;
        if (w <= 0 || h <= 0 || w > 2000 || h > 2000) return gl;
        var acc = new float[w * h + 4];
        foreach (var ct in contours)
        {
            int n = ct.Count;
            // перша точка на кривій (якщо всі поза - середина між першими двома)
            int first = -1;
            for (int i = 0; i < n; i++) if (ct[i].on) { first = i; break; }
            (float x, float y) P(int i) => (ct[i].x * s - left, -ct[i].y * s - top);
            (float x, float y) start;
            // обхід: від першої точки на кривій (або від середини P0-P1,
            // якщо всі точки поза кривою), назад до старту - окремо
            int count;
            if (first < 0)
            {
                var a0 = P(0); var a1 = P(1 % n);
                start = ((a0.x + a1.x) / 2, (a0.y + a1.y) / 2);
                first = 0;
                count = n;
            }
            else { start = P(first); count = n - 1; }
            var cur = start;
            (float x, float y)? ctrl = null;
            for (int k = 1; k <= count; k++)
            {
                int i = (first + k) % n;
                var pt = P(i);
                if (ct[i].on)
                {
                    if (ctrl is { } cp) Quad(acc, w, h, cur, cp, pt);
                    else Line(acc, w, h, cur, pt);
                    cur = pt;
                    ctrl = null;
                }
                else
                {
                    if (ctrl is { } cp2)
                    {
                        var mid = ((cp2.x + pt.x) / 2, (cp2.y + pt.y) / 2);
                        Quad(acc, w, h, cur, cp2, mid);
                        cur = mid;
                    }
                    ctrl = pt;
                }
            }
            if (ctrl is { } last) Quad(acc, w, h, cur, last, start);
            else Line(acc, w, h, cur, start);
        }
        gl.Alpha = new byte[w * h];
        float sum = 0;
        for (int i = 0; i < w * h; i++)
        {
            sum += acc[i];
            float cov = Math.Min(Math.Abs(sum), 1f);
            gl.Alpha[i] = Gamma[(int)(cov * 255 + 0.5f)];
        }
        gl.W = w; gl.H = h; gl.Left = left; gl.Top = top;
        return gl;
    }

    // Легка гама: напівпокриті пікселі трохи щільніші - тонкі лінії
    // світлого тексту на темному тлі не "розсипаються".
    private static readonly byte[] Gamma = MakeGamma();
    private static byte[] MakeGamma()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = (byte)Math.Round(255 * Math.Pow(i / 255.0, 1 / 1.3));
        return t;
    }

    private static void Quad(float[] acc, int w, int h, (float x, float y) p0, (float x, float y) p1, (float x, float y) p2)
    {
        float ddx = p0.x - 2 * p1.x + p2.x, ddy = p0.y - 2 * p1.y + p2.y;
        float devsq = ddx * ddx + ddy * ddy;
        if (devsq < 0.333f) { Line(acc, w, h, p0, p2); return; }
        int n = 1 + (int)Math.Floor(Math.Sqrt(Math.Sqrt(3 * devsq)));
        var prev = p0;
        for (int i = 1; i <= n; i++)
        {
            float t = (float)i / n, mt = 1 - t;
            var p = (mt * mt * p0.x + 2 * mt * t * p1.x + t * t * p2.x, mt * mt * p0.y + 2 * mt * t * p1.y + t * t * p2.y);
            Line(acc, w, h, prev, p);
            prev = p;
        }
    }

    // Відрізок: накопичити знакову площу під ним у кожному пікселі рядків.
    private static void Line(float[] acc, int w, int h, (float x, float y) p0, (float x, float y) p1)
    {
        if (p0.y == p1.y) return;
        float dir = 1;
        if (p0.y > p1.y) { dir = -1; (p0, p1) = (p1, p0); }
        float dxdy = (p1.x - p0.x) / (p1.y - p0.y);
        float x = p0.x;
        if (p0.y < 0) x -= p0.y * dxdy;
        int yEnd = Math.Min(h, (int)Math.Ceiling(p1.y));
        for (int y = Math.Max(0, (int)p0.y); y < yEnd; y++)
        {
            int ls = y * w;
            float dy = Math.Min(y + 1f, p1.y) - Math.Max((float)y, p0.y);
            float xnext = x + dxdy * dy;
            float d = dy * dir;
            float x0 = Math.Min(x, xnext), x1 = Math.Max(x, xnext);
            x0 = Math.Max(0, x0);
            x1 = Math.Max(0, x1);
            float x0f = (float)Math.Floor(x0);
            int x0i = (int)x0f;
            float x1c = (float)Math.Ceiling(x1);
            int x1i = (int)x1c;
            if (x0i >= w) { x = xnext; continue; }
            if (x1i <= x0i + 1)
            {
                float xmf = 0.5f * (x + xnext) - x0f;
                Add(acc, ls + x0i, d - d * xmf);
                Add(acc, ls + x0i + 1, d * xmf);
            }
            else
            {
                float rs = 1 / (x1 - x0);
                float x0fr = x0 - x0f;
                float a0 = 0.5f * rs * (1 - x0fr) * (1 - x0fr);
                float x1fr = x1 - x1c + 1;
                float am = 0.5f * rs * x1fr * x1fr;
                Add(acc, ls + x0i, d * a0);
                if (x1i == x0i + 2) Add(acc, ls + x0i + 1, d * (1 - a0 - am));
                else
                {
                    float a1 = rs * (1.5f - x0fr);
                    Add(acc, ls + x0i + 1, d * (a1 - a0));
                    for (int xi = x0i + 2; xi < x1i - 1; xi++) Add(acc, ls + xi, d * rs);
                    float a2 = a1 + (x1i - x0i - 3) * rs;
                    Add(acc, ls + x1i - 1, d * (1 - a2 - am));
                }
                Add(acc, ls + x1i, d * am);
            }
            x = xnext;
        }
    }

    private static void Add(float[] acc, int i, float v)
    {
        if (i >= 0 && i < acc.Length) acc[i] += v;
    }

    // ---------- вбудовані шрифти ----------

    private static readonly Dictionary<string, TtfFont> _faces = new();

    // face: "sans" | "mono"; bold - жирний.
    public static TtfFont Face(string face, bool bold)
    {
        string file = face == "mono"
            ? (bold ? "NotoSansMono-Bold.ttf" : "NotoSansMono-Regular.ttf")
            : (bold ? "NotoSans-Bold.ttf" : "NotoSans-Regular.ttf");
        lock (_faces)
        {
            if (_faces.TryGetValue(file, out var f)) return f;
            using var st = typeof(TtfFont).Assembly.GetManifestResourceStream("NyxilumLang.Fonts." + file)
                ?? throw new Exception("Нема вбудованого шрифту " + file);
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            f = Load(ms.ToArray());
            _faces[file] = f;
            return f;
        }
    }
}
