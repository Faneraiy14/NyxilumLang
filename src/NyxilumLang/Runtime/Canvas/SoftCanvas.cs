namespace NyxilumLang.Runtime.Canvas;

// Програмне полотно: масив пікселів у пам'яті (0x00RRGGBB), у який
// surface*-функції малюють ОДНАКОВО на всіх платформах. Платформа (X11 /
// Windows Forms) лише показує готовий масив (ISurfaceBackend.Present) і
// постачає події миші/клавіатури - тому малюнок на Linux і Windows
// піксель-у-піксель той самий.
//
// Колір - одне число 0xRRGGBB. Старший байт, якщо не 0, - прозорість
// (0x80RRGGBB = напівпрозорий, 0xFFRRGGBB = непрозорий, як і 0x00RRGGBB).
//
// Обрізання (clip): усе малювання обмежене прямокутником Clip - потрібно
// для панелей IDE (текст редактора не налазить на сусідню панель).
public sealed class SoftCanvas
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] Pixels { get; private set; }

    private int _cx0, _cy0, _cx1, _cy1; // clip: [x0, x1) x [y0, y1)
    private readonly Stack<(int, int, int, int)> _clipStack = new();

    public SoftCanvas(int width, int height)
    {
        Width = Math.Max(1, width); Height = Math.Max(1, height);
        Pixels = new uint[Width * Height];
        ResetClip();
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        var old = Pixels; int ow = Width, oh = Height;
        Pixels = new uint[width * height];
        for (int y = 0; y < Math.Min(oh, height); y++)
            Array.Copy(old, y * ow, Pixels, y * width, Math.Min(ow, width));
        Width = width; Height = height;
        _clipStack.Clear();
        ResetClip();
    }

    private void ResetClip() { _cx0 = 0; _cy0 = 0; _cx1 = Width; _cy1 = Height; }

    // Новий clip = перетин з поточним (вкладені панелі не виходять за батьківську).
    public void PushClip(int x, int y, int w, int h)
    {
        _clipStack.Push((_cx0, _cy0, _cx1, _cy1));
        _cx0 = Math.Max(_cx0, x); _cy0 = Math.Max(_cy0, y);
        _cx1 = Math.Min(_cx1, x + w); _cy1 = Math.Min(_cy1, y + h);
        if (_cx1 < _cx0) _cx1 = _cx0;
        if (_cy1 < _cy0) _cy1 = _cy0;
    }

    public void PopClip()
    {
        if (_clipStack.Count > 0) (_cx0, _cy0, _cx1, _cy1) = _clipStack.Pop();
        else ResetClip();
    }

    private static uint Blend(uint dst, uint src, uint a)
    {
        uint ia = 255 - a;
        uint r = ((src >> 16 & 0xff) * a + (dst >> 16 & 0xff) * ia) / 255;
        uint g = ((src >> 8 & 0xff) * a + (dst >> 8 & 0xff) * ia) / 255;
        uint b = ((src & 0xff) * a + (dst & 0xff) * ia) / 255;
        return r << 16 | g << 8 | b;
    }

    private void Plot(int x, int y, uint color)
    {
        if (x < _cx0 || y < _cy0 || x >= _cx1 || y >= _cy1) return;
        uint a = color >> 24;
        int i = y * Width + x;
        Pixels[i] = a == 0 || a == 255 ? color & 0xffffff : Blend(Pixels[i], color, a);
    }

    private void HLine(int x0, int x1, int y, uint color) // [x0, x1)
    {
        if (y < _cy0 || y >= _cy1) return;
        x0 = Math.Max(x0, _cx0); x1 = Math.Min(x1, _cx1);
        if (x1 <= x0) return;
        uint a = color >> 24;
        int row = y * Width;
        if (a == 0 || a == 255) Array.Fill(Pixels, color & 0xffffff, row + x0, x1 - x0);
        else for (int x = x0; x < x1; x++) Pixels[row + x] = Blend(Pixels[row + x], color, a);
    }

    public void Clear(uint color)
    {
        _clipStack.Clear(); ResetClip();
        Array.Fill(Pixels, color & 0xffffff);
    }

    public void FillRect(int x, int y, int w, int h, uint color)
    {
        int y0 = Math.Max(y, _cy0), y1 = Math.Min(y + h, _cy1);
        for (int yy = y0; yy < y1; yy++) HLine(x, x + w, yy, color);
    }

    public void StrokeRect(int x, int y, int w, int h, uint color, int t = 1)
    {
        if (w <= 0 || h <= 0) return;
        t = Math.Max(1, Math.Min(t, Math.Min(w, h) / 2 + 1));
        FillRect(x, y, w, t, color);
        FillRect(x, y + h - t, w, t, color);
        FillRect(x, y + t, t, h - 2 * t, color);
        FillRect(x + w - t, y + t, t, h - 2 * t, color);
    }

    // Прямокутник із заокругленими кутами радіуса r - для блоків і кнопок.
    public void FillRoundRect(int x, int y, int w, int h, int r, uint color)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2));
        for (int yy = 0; yy < h; yy++)
        {
            int inset = 0;
            int dy = yy < r ? r - yy - 1 : yy >= h - r ? yy - (h - r) : -1;
            if (dy >= 0)
            {
                double dx = Math.Sqrt(Math.Max(0, r * r - (dy + 0.5) * (dy + 0.5)));
                inset = r - (int)Math.Round(dx);
            }
            HLine(x + inset, x + w - inset, y + yy, color);
        }
    }

    public void FillCircle(int cx, int cy, int radius, uint color)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            int dx = (int)Math.Round(Math.Sqrt(Math.Max(0, radius * radius - dy * dy)));
            HLine(cx - dx, cx + dx + 1, cy + dy, color);
        }
    }

    public void StrokeCircle(int cx, int cy, int radius, uint color, int t = 1)
    {
        int rOut = radius, rIn = Math.Max(0, radius - Math.Max(1, t));
        for (int dy = -rOut; dy <= rOut; dy++)
        {
            int xo = (int)Math.Round(Math.Sqrt(Math.Max(0, rOut * rOut - dy * dy)));
            if (Math.Abs(dy) >= rIn) { HLine(cx - xo, cx + xo + 1, cy + dy, color); continue; }
            int xi = (int)Math.Round(Math.Sqrt(Math.Max(0, rIn * rIn - dy * dy)));
            HLine(cx - xo, cx - xi, cy + dy, color);
            HLine(cx + xi + 1, cx + xo + 1, cy + dy, color);
        }
    }

    // Лінія Брезенхема; товщина t - квадратний "пензель" у кожній точці.
    public void Line(int x0, int y0, int x1, int y1, uint color, int t = 1)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy, half = (t - 1) / 2;
        for (int guard = 0; guard < 100000; guard++)
        {
            if (t <= 1) Plot(x0, y0, color);
            else FillRect(x0 - half, y0 - half, t, t, color);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // Заливка довільного многокутника (парно-непарне правило, сканлайни) -
    // "вушка" й "виїмки" блоків, трикутник кнопки "Запустити".
    public void FillPolygon(IReadOnlyList<(double X, double Y)> pts, uint color)
    {
        if (pts.Count < 3) return;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in pts) { minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
        int y0 = Math.Max(_cy0, (int)Math.Floor(minY)), y1 = Math.Min(_cy1 - 1, (int)Math.Ceiling(maxY));
        var xs = new List<double>();
        for (int y = y0; y <= y1; y++)
        {
            double sy = y + 0.5;
            xs.Clear();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                if ((a.Y <= sy && b.Y > sy) || (b.Y <= sy && a.Y > sy))
                    xs.Add(a.X + (sy - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
                HLine((int)Math.Round(xs[k]), (int)Math.Round(xs[k + 1]), y, color);
        }
    }

    // ---- Текст ----

    // Поточний шрифт тексту (surfaceFont): "pixel" - Terminus, "sans"/"mono" - TTF.
    public static string FontFace = "pixel";

    // TTF: розмір гліфа (em) і базова лінія для "клітинки" висотою size -
    // щоб розкладка, розрахована на піксельний шрифт, не зсувалась.
    private static (TtfFont f, float em, int baseline) Ttf(int size, bool bold)
    {
        var f = TtfFont.Face(FontFace, bold);
        float em = size * 0.94f;
        float s = em / f.UnitsPerEm;
        float asc = f.Ascender * s, desc = -f.Descender * s;
        int baseline = (int)Math.Round((size - (asc + desc)) / 2 + asc);
        return (f, em, baseline);
    }

    // Гліфа нема в поточному TTF: інший Noto, далі піксельний Terminus
    // (стрілки, √, ▶... - у Noto Sans їх нема). null - Terminus.
    private static TtfFont? FallbackFace(TtfFont f, int cp, bool bold)
    {
        if (f.HasGlyph(cp)) return f;
        var other = TtfFont.Face(FontFace == "mono" ? "sans" : "mono", bold);
        if (other.HasGlyph(cp)) return other;
        return null;
    }

    private static float CharWidth(TtfFont f, float em, int cp, int size, bool bold)
    {
        var ff = FallbackFace(f, cp, bold);
        if (ff != null) return ff.AdvanceUnits(ff.GlyphIndex(cp)) * em / ff.UnitsPerEm;
        var (pf, scale) = PsfFont.ForSize(size, bold);
        return pf.Width * scale;
    }

    private static float TtfWidth(string text, int size, bool bold)
    {
        var (f, em, _) = Ttf(size, bold);
        float s = em / f.UnitsPerEm, w = 0;
        float space = f.AdvanceUnits(f.GlyphIndex(' ')) * s;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(text[i], text[i + 1]); i++; }
            else if (char.IsSurrogate(text[i])) cp = 0xFFFD; // половинка емодзі - не падати
            if (cp == '\t') { w += space * 4; continue; }
            w += CharWidth(f, em, cp, size, bold);
        }
        return w;
    }

    private int DrawTtf(string text, int x, int y, int size, uint color, bool bold)
    {
        var (f, em, baseline) = Ttf(size, bold);
        if (y >= _cy1 || y + size * 2 <= _cy0) return (int)Math.Round(TtfWidth(text, size, bold));
        float s = em / f.UnitsPerEm;
        float pen = x;
        float space = f.AdvanceUnits(f.GlyphIndex(' ')) * s;
        uint ca = color >> 24;
        if (ca == 0) ca = 255;
        uint col = color & 0xffffff;
        int by = y + baseline;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(text[i], text[i + 1]); i++; }
            else if (char.IsSurrogate(text[i])) cp = 0xFFFD; // половинка емодзі - не падати
            if (cp == '\t') { pen += space * 4; continue; }
            var ff = FallbackFace(f, cp, bold);
            if (ff == null)
            {
                // піксельний гліф Terminus у тій самій клітинці
                var (pf, sc) = PsfFont.ForSize(size, bold);
                int pg = pf.GlyphIndex(cp);
                int px0 = (int)Math.Round(pen), py0 = y + (size - pf.Height * sc) / 2;
                for (int gy2 = 0; gy2 < pf.Height; gy2++)
                    for (int gx2 = 0; gx2 < pf.Width; gx2++)
                        if (pf.Pixel(pg, gx2, gy2))
                        {
                            if (sc == 1) Plot(px0 + gx2, py0 + gy2, color);
                            else FillRect(px0 + gx2 * sc, py0 + gy2 * sc, sc, sc, color);
                        }
                pen += pf.Width * sc;
                continue;
            }
            var g = ff.Render(ff.GlyphIndex(cp), em);
            int gx = (int)Math.Round(pen) + g.Left, gy = by + g.Top;
            if (g.W > 0 && gx < _cx1 && gx + g.W > _cx0 && gy < _cy1 && gy + g.H > _cy0)
            {
                int y0 = Math.Max(gy, _cy0), y1 = Math.Min(gy + g.H, _cy1);
                int x0 = Math.Max(gx, _cx0), x1 = Math.Min(gx + g.W, _cx1);
                for (int yy = y0; yy < y1; yy++)
                {
                    int src = (yy - gy) * g.W - gx, dst = yy * Width;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        uint a = g.Alpha[src + xx];
                        if (a == 0) continue;
                        a = a * ca / 255;
                        Pixels[dst + xx] = a >= 255 ? col : Blend(Pixels[dst + xx], col, a);
                    }
                }
            }
            pen += g.Advance;
        }
        return (int)Math.Round(pen - x);
    }

    public static int TextWidth(string text, int size, bool bold = false)
    {
        if (FontFace != "pixel") return (int)Math.Round(TtfWidth(text, size, bold));
        var (font, scale) = PsfFont.ForSize(size, bold);
        int n = 0;
        for (int i = 0; i < text.Length; i++) { if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++; n++; }
        return n * font.Width * scale;
    }

    public static int TextHeight(int size, bool bold = false)
    {
        if (FontFace != "pixel") return size;
        var (font, scale) = PsfFont.ForSize(size, bold);
        return font.Height * scale;
    }

    // Малює рядок, (x, y) - лівий верхній кут. Повертає ширину в пікселях.
    // '\t' - як 4 пробіли; інших керуючих символів шрифт не має.
    public int DrawText(string text, int x, int y, int size, uint color, bool bold = false)
    {
        if (FontFace != "pixel") return DrawTtf(text, x, y, size, color, bold);
        var (font, scale) = PsfFont.ForSize(size, bold);
        int gw = font.Width * scale, gh = font.Height * scale;
        int cx = x;
        if (y >= _cy1 || y + gh <= _cy0) return TextWidth(text, size, bold);
        for (int i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(text[i], text[i + 1]); i++; }
            else if (char.IsSurrogate(text[i])) cp = 0xFFFD; // половинка емодзі - не падати
            if (cp == '\t') { cx += gw * 4; continue; }
            if (cp != ' ' && cx + gw > _cx0 && cx < _cx1)
            {
                int g = font.GlyphIndex(cp);
                // Швидкий шлях: гліф цілком у clip, колір непрозорий, без
                // масштабу - пишемо в масив напряму, без перевірок на піксель.
                uint alpha = color >> 24;
                if (scale == 1 && (alpha == 0 || alpha == 255) && cx >= _cx0 && y >= _cy0 && cx + gw <= _cx1 && y + gh <= _cy1)
                {
                    uint col = color & 0xffffff;
                    int fw = font.Width, baseIdx = y * Width + cx;
                    foreach (int off in font.OnPixels(g))
                    {
                        int py = off / fw;
                        Pixels[baseIdx + py * Width + (off - py * fw)] = col;
                    }
                }
                else
                for (int gy = 0; gy < font.Height; gy++)
                    for (int gx = 0; gx < font.Width; gx++)
                        if (font.Pixel(g, gx, gy))
                        {
                            if (scale == 1) Plot(cx + gx, y + gy, color);
                            else FillRect(cx + gx * scale, y + gy * scale, scale, scale, color);
                        }
            }
            cx += gw;
        }
        return cx - x;
    }

    // Масштабоване (найближчий піксель) копіювання src у прямокутник dx,dy,dw,dh.
    public void BlitScaled(SoftCanvas src, int dx, int dy, int dw, int dh)
    {
        if (dw <= 0 || dh <= 0) return;
        int y0 = Math.Max(dy, _cy0), y1 = Math.Min(dy + dh, _cy1);
        int x0 = Math.Max(dx, _cx0), x1 = Math.Min(dx + dw, _cx1);
        for (int y = y0; y < y1; y++)
        {
            int sy = (int)((long)(y - dy) * src.Height / dh);
            int srow = sy * src.Width, drow = y * Width;
            for (int x = x0; x < x1; x++)
            {
                int sx = (int)((long)(x - dx) * src.Width / dw);
                Pixels[drow + x] = src.Pixels[srow + sx];
            }
        }
    }

    // Копія області іншого полотна (або цього ж) - для зображень/спрайтів.
    public void Blit(SoftCanvas src, int dx, int dy)
    {
        for (int y = 0; y < src.Height; y++)
        {
            int ty = dy + y;
            if (ty < _cy0 || ty >= _cy1) continue;
            for (int x = 0; x < src.Width; x++)
            {
                int tx = dx + x;
                if (tx < _cx0 || tx >= _cx1) continue;
                Pixels[ty * Width + tx] = src.Pixels[y * src.Width + x];
            }
        }
    }
}
