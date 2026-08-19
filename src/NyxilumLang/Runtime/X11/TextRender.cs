using System.Collections.Generic;

namespace NyxilumLang.Runtime.X11;

// Портовано з rawgui/src/text-render.js - текст як список прямокутників
// для PolyFillRectangle. Кожен "увімкнений" піксель шрифту (Font5x7) стає
// одним прямокутником scale x scale - не найшвидше кодування (сусідні
// пікселі рядка можна було б зливати в один ширший прямокутник), але
// найпростіше й найлегше перевіряється - достатньо для міток/кнопок,
// не для абзаців тексту.
public static class TextRender
{
    public static List<X11Protocol.Rect> BuildTextRects(string text, int x, int y, int scale = 2, int letterSpacing = 1)
    {
        var rects = new List<X11Protocol.Rect>();
        int cursorX = x;
        foreach (var ch in text)
        {
            var glyph = Font5x7.GetGlyph(ch);
            for (int row = 0; row < Font5x7.GlyphHeight; row++)
            {
                var bits = glyph[row];
                for (int col = 0; col < Font5x7.GlyphWidth; col++)
                {
                    if (bits[col] == '1')
                        rects.Add(new X11Protocol.Rect(
                            (short)(cursorX + col * scale), (short)(y + row * scale),
                            (ushort)scale, (ushort)scale));
                }
            }
            cursorX += (Font5x7.GlyphWidth + letterSpacing) * scale;
        }
        return rects;
    }

    public static (int width, int height) MeasureText(string text, int scale = 2, int letterSpacing = 1)
    {
        int width = text.Length * (Font5x7.GlyphWidth + letterSpacing) * scale - letterSpacing * scale;
        int height = Font5x7.GlyphHeight * scale;
        return (System.Math.Max(width, 0), height);
    }
}
