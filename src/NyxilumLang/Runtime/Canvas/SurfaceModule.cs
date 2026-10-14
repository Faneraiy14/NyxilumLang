namespace NyxilumLang.Runtime.Canvas;

// surface* - кросплатформне вікно-полотно (Linux/Mac X11, Windows Forms):
// пікселі малюються в пам'яті (SoftCanvas), однаково на всіх платформах,
// і показуються surfacePresent. Події миші/клавіатури - surfaceNextEvent
// (див. опис полів у Surface.cs). Основа для IDE та ігор на NyxilumLang.
//
// Колір - одне число 0xRRGGBB (або rgb(r, g, b)); 0xAARRGGBB - з прозорістю.
public static class SurfaceModule
{
    private static Surface S(object? o) => o as Surface ?? throw new Exception("Очікувалося полотно (результат surfaceOpen)");
    private static int I(object? o) => o is double d ? (int)d : Convert.ToInt32(o);
    private static uint C(object? o) => o is double d ? (uint)(long)d : Convert.ToUInt32(o);
    private static int OptI(object[] a, int i, int def) => a.Length > i && a[i] != null ? I(a[i]) : def;
    private static bool OptB(object[] a, int i) => a.Length > i && a[i] is bool b && b;

    // Масштаб інтерфейсу (surfaceScale): програма малює й отримує події в
    // логічних пікселях, полотно - у фізичних (x масштаб). Текст TTF
    // рендериться у фізичному розмірі - чіткий на будь-якому масштабі.
    public static double Scale = 1;
    private static int X(object? v) => Scale == 1 ? I(v) : (int)Math.Round(D(v) * Scale);
    private static double D(object? o) => o is double d ? d : Convert.ToDouble(o);
    private static int T(int t) => Scale == 1 ? t : Math.Max(1, (int)Math.Round(t * Scale));
    // прямокутник: краї округлюються окремо - сусідні панелі без щілин
    private static (int x, int y, int w, int h) R(object? x, object? y, object? w, object? h)
    {
        if (Scale == 1) return (I(x), I(y), I(w), I(h));
        int x0 = X(x), y0 = X(y);
        return (x0, y0, (int)Math.Round((D(x) + D(w)) * Scale) - x0, (int)Math.Round((D(y) + D(h)) * Scale) - y0);
    }
    private static int Sz(int size) => Scale == 1 ? size : Math.Max(1, (int)Math.Round(size * Scale));

    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public static void Register(Dictionary<string, Func<object[], object?>> r)
    {
        r["rgb"] = a => (double)((uint)Math.Clamp(I(a[0]), 0, 255) << 16 | (uint)Math.Clamp(I(a[1]), 0, 255) << 8 | (uint)Math.Clamp(I(a[2]), 0, 255));
        r["rgba"] = a => (double)((uint)Math.Clamp(I(a[3]), 0, 255) << 24 | (uint)Math.Clamp(I(a[0]), 0, 255) << 16 | (uint)Math.Clamp(I(a[1]), 0, 255) << 8 | (uint)Math.Clamp(I(a[2]), 0, 255));

        // timeMs() - монотонний годинник у мілісекундах (від старту процесу):
        // анімації, подвійний клік, заміри швидкості. timestamp() - секунди.
        r["timeMs"] = a => (double)_clock.ElapsedMilliseconds;

        r["surfaceOpen"] = a =>
        {
            Sandbox.CheckGui();
            string title = a[0]?.ToString() ?? "NyxilumLang";
            int w = I(a[1]), h = I(a[2]);
            var s = new Surface(w, h);
            // NX_HEADLESS=1 - полотно без вікна (автотести, CI без дисплея):
            // малювання, surfaceSave і surfacePostEvent працюють як завжди.
            // NX_STAGE_DIR - програма запущена з Nx IDE: кадри - у сцену IDE
            // (важливіше за NX_HEADLESS, яке успадковується від тестів IDE)
            var stage = Environment.GetEnvironmentVariable("NX_STAGE_DIR");
            if (!string.IsNullOrEmpty(stage)) { s.Backend = new StageBackend(s, stage); return s; }
            if (Environment.GetEnvironmentVariable("NX_HEADLESS") == "1") { s.Backend = new HeadlessSurfaceBackend(); return s; }
#if WINDOWS
            s.Backend = new WinFormsSurfaceBackend(s, title, w, h);
#else
            s.Backend = new X11SurfaceBackend(s, title, w, h);
#endif
            return s;
        };
        r["surfaceClose"] = a => { var s = S(a[0]); s.Closed = true; s.Backend.Close(); return null; };
        r["surfaceIsOpen"] = a => !S(a[0]).Closed;
        r["surfaceWidth"] = a => Math.Floor(S(a[0]).Canvas.Width / Scale);
        r["surfaceHeight"] = a => Math.Floor(S(a[0]).Canvas.Height / Scale);
        // surfaceScale(k) -> попередній: масштаб інтерфейсу (1 = 100%, 1.5 = 150%)
        r["surfaceScale"] = a =>
        {
            double prev = Scale;
            if (a.Length > 0 && a[0] != null) Scale = Math.Clamp(D(a[0]), 0.5, 4);
            return prev;
        };
        r["surfaceTitle"] = a => { S(a[0]).Backend.SetTitle(a[1]?.ToString() ?? ""); return null; };
        r["surfacePresent"] = a => { var s = S(a[0]); if (!s.Closed) s.Backend.Present(s.Canvas); return null; };

        r["surfaceClear"] = a => { S(a[0]).Canvas.Clear(C(a[1])); return null; };
        r["surfaceRect"] = a => { var q = R(a[1], a[2], a[3], a[4]); S(a[0]).Canvas.FillRect(q.x, q.y, q.w, q.h, C(a[5])); return null; };
        r["surfaceRectLine"] = a => { var q = R(a[1], a[2], a[3], a[4]); S(a[0]).Canvas.StrokeRect(q.x, q.y, q.w, q.h, C(a[5]), T(OptI(a, 6, 1))); return null; };
        r["surfaceRoundRect"] = a => { var q = R(a[1], a[2], a[3], a[4]); S(a[0]).Canvas.FillRoundRect(q.x, q.y, q.w, q.h, X(a[5]), C(a[6])); return null; };
        r["surfaceCircle"] = a => { S(a[0]).Canvas.FillCircle(X(a[1]), X(a[2]), X(a[3]), C(a[4])); return null; };
        r["surfaceCircleLine"] = a => { S(a[0]).Canvas.StrokeCircle(X(a[1]), X(a[2]), X(a[3]), C(a[4]), T(OptI(a, 5, 1))); return null; };
        r["surfaceLine"] = a => { S(a[0]).Canvas.Line(X(a[1]), X(a[2]), X(a[3]), X(a[4]), C(a[5]), T(OptI(a, 6, 1))); return null; };
        // surfacePolygon(s, [x1, y1, x2, y2, ...], колір)
        r["surfacePolygon"] = a =>
        {
            var flat = a[1] as List<object> ?? throw new Exception("surfacePolygon: другий аргумент - масив [x1, y1, x2, y2, ...]");
            var pts = new List<(double, double)>(flat.Count / 2);
            for (int i = 0; i + 1 < flat.Count; i += 2) pts.Add((Convert.ToDouble(flat[i]) * Scale, Convert.ToDouble(flat[i + 1]) * Scale));
            S(a[0]).Canvas.FillPolygon(pts, C(a[2]));
            return null;
        };
        // surfaceText(s, текст, x, y, розмір, колір, жирний?) -> ширина в пікселях
        r["surfaceText"] = a =>
        {
            int w = S(a[0]).Canvas.DrawText(a[1]?.ToString() ?? "", X(a[2]), X(a[3]), Sz(I(a[4])), C(a[5]), OptB(a, 6));
            return Scale == 1 ? w : Math.Round(w / Scale);
        };
        r["textWidth"] = a =>
        {
            int w = SoftCanvas.TextWidth(a[0]?.ToString() ?? "", Sz(I(a[1])), OptB(a, 2));
            return Scale == 1 ? w : Math.Round(w / Scale);
        };
        r["textHeight"] = a => Scale == 1 ? SoftCanvas.TextHeight(I(a[0]), OptB(a, 1)) : Math.Round(SoftCanvas.TextHeight(Sz(I(a[0])), OptB(a, 1)) / Scale);
        // surfaceFont("pixel" | "sans" | "mono") -> попередній. Шрифт для
        // наступних surfaceText/textWidth/textHeight: піксельний Terminus
        // (типово) або згладжені Noto Sans / Noto Sans Mono.
        r["surfaceFont"] = a =>
        {
            string prev = SoftCanvas.FontFace;
            string f = a.Length > 0 ? a[0]?.ToString() ?? "pixel" : "pixel";
            if (f != "pixel" && f != "sans" && f != "mono") throw new Exception("surfaceFont: \"pixel\", \"sans\" або \"mono\"");
            SoftCanvas.FontFace = f;
            return prev;
        };
        r["surfaceGetPixel"] = a =>
        {
            var c = S(a[0]).Canvas; int x = X(a[1]), y = X(a[2]);
            return x < 0 || y < 0 || x >= c.Width || y >= c.Height ? -1.0 : (double)c.Pixels[y * c.Width + x];
        };
        // surfaceDrawFrame(s, файл, x, y, w, h) - кадр сцени (frame.raw від
        // програми з NX_STAGE_DIR), вписаний у прямокутник зі збереженням
        // пропорцій. Повертає [ширина кадру, висота кадру] або null.
        r["surfaceDrawFrame"] = a =>
        {
            var frame = StageBackend.LoadFrame(a[1]?.ToString() ?? "");
            if (frame == null) return null;
            var q = R(a[2], a[3], a[4], a[5]);
            int x = q.x, y = q.y, w = q.w, h = q.h;
            double k = Math.Min((double)w / frame.Width, (double)h / frame.Height);
            int dw = (int)(frame.Width * k), dh = (int)(frame.Height * k);
            S(a[0]).Canvas.BlitScaled(frame, x + (w - dw) / 2, y + (h - dh) / 2, dw, dh);
            return new List<object> { (double)frame.Width, (double)frame.Height };
        };
        r["surfaceClip"] = a => { var q = R(a[1], a[2], a[3], a[4]); S(a[0]).Canvas.PushClip(q.x, q.y, q.w, q.h); return null; };
        r["surfaceUnclip"] = a => { S(a[0]).Canvas.PopClip(); return null; };

        // surfaceNextEvent(s, чекатиМс?) - подія (мапа) або null. Без другого
        // аргументу чекає, доки подія не прийде (типовий цикл IDE).
        r["surfaceNextEvent"] = a =>
        {
            var e = S(a[0]).NextEvent(OptI(a, 1, -1));
            if (e == null || Scale == 1) return e;
            // у черзі - фізичні пікселі; програмі - логічні
            var copy = new Dictionary<string, object>(e);
            foreach (var key in new[] { "x", "y", "width", "height" })
                if (copy.TryGetValue(key, out var v) && v is double dv) copy[key] = Math.Floor(dv / Scale);
            return copy;
        };
        r["surfaceMouseX"] = a => Math.Floor(S(a[0]).MouseX / Scale);
        r["surfaceMouseY"] = a => Math.Floor(S(a[0]).MouseY / Scale);
        r["surfaceMouseDown"] = a => S(a[0]).MouseDown;
        r["surfaceKeyDown"] = a => S(a[0]).KeyDown(a[1]?.ToString() ?? "");

        // surfaceSave(s, "кадр.png") - поточні пікселі полотна у PNG.
        r["surfaceSave"] = a =>
        {
            var path = a[1]?.ToString() ?? "";
            Sandbox.CheckPath(path);
            PngWriter.Save(S(a[0]).Canvas, path);
            return null;
        };
        // surfacePostEvent(s, подія) - підкласти подію в чергу так, ніби її
        // зробив користувач (мапа з полем type, як у surfaceNextEvent).
        // Для автотестів IDE і для гайду, що сам показує кліки.
        r["surfacePostEvent"] = a =>
        {
            // Приймає і структуру (поля e.type), і мапу (newMap/mapSet).
            var fields = a[1] switch
            {
                Dictionary<string, object> d => d,
                NxMap m => m.Entries.Where(kv => kv.Key is string).ToDictionary(kv => (string)kv.Key, kv => kv.Value),
                _ => throw new Exception("surfacePostEvent: другий аргумент - подія (структура чи мапа) з полем type")
            };
            if (!fields.TryGetValue("type", out var type) || type is not string)
                throw new Exception("surfacePostEvent: подія мусить мати поле type (рядок)");
            var e = Surface.MakeEvent((string)type);
            foreach (var kv in fields) e[kv.Key] = kv.Value;
            // подія в логічних пікселях -> у черзі фізичні (як від вікна)
            if (Scale != 1)
                foreach (var key in new[] { "x", "y", "width", "height" })
                    if (e.TryGetValue(key, out var v) && v is double dv) e[key] = Math.Round(dv * Scale);
            S(a[0]).Post(e);
            return null;
        };
        r["clipboardGet"] = a => NxClipboard.Get();
        r["clipboardSet"] = a => { NxClipboard.Set(a[0]?.ToString() ?? ""); return null; };
    }

}
