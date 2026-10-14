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
        r["surfaceWidth"] = a => (double)S(a[0]).Canvas.Width;
        r["surfaceHeight"] = a => (double)S(a[0]).Canvas.Height;
        r["surfaceTitle"] = a => { S(a[0]).Backend.SetTitle(a[1]?.ToString() ?? ""); return null; };
        r["surfacePresent"] = a => { var s = S(a[0]); if (!s.Closed) s.Backend.Present(s.Canvas); return null; };

        r["surfaceClear"] = a => { S(a[0]).Canvas.Clear(C(a[1])); return null; };
        r["surfaceRect"] = a => { S(a[0]).Canvas.FillRect(I(a[1]), I(a[2]), I(a[3]), I(a[4]), C(a[5])); return null; };
        r["surfaceRectLine"] = a => { S(a[0]).Canvas.StrokeRect(I(a[1]), I(a[2]), I(a[3]), I(a[4]), C(a[5]), OptI(a, 6, 1)); return null; };
        r["surfaceRoundRect"] = a => { S(a[0]).Canvas.FillRoundRect(I(a[1]), I(a[2]), I(a[3]), I(a[4]), I(a[5]), C(a[6])); return null; };
        r["surfaceCircle"] = a => { S(a[0]).Canvas.FillCircle(I(a[1]), I(a[2]), I(a[3]), C(a[4])); return null; };
        r["surfaceCircleLine"] = a => { S(a[0]).Canvas.StrokeCircle(I(a[1]), I(a[2]), I(a[3]), C(a[4]), OptI(a, 5, 1)); return null; };
        r["surfaceLine"] = a => { S(a[0]).Canvas.Line(I(a[1]), I(a[2]), I(a[3]), I(a[4]), C(a[5]), OptI(a, 6, 1)); return null; };
        // surfacePolygon(s, [x1, y1, x2, y2, ...], колір)
        r["surfacePolygon"] = a =>
        {
            var flat = a[1] as List<object> ?? throw new Exception("surfacePolygon: другий аргумент - масив [x1, y1, x2, y2, ...]");
            var pts = new List<(double, double)>(flat.Count / 2);
            for (int i = 0; i + 1 < flat.Count; i += 2) pts.Add((Convert.ToDouble(flat[i]), Convert.ToDouble(flat[i + 1])));
            S(a[0]).Canvas.FillPolygon(pts, C(a[2]));
            return null;
        };
        // surfaceText(s, текст, x, y, розмір, колір, жирний?) -> ширина в пікселях
        r["surfaceText"] = a => (double)S(a[0]).Canvas.DrawText(a[1]?.ToString() ?? "", I(a[2]), I(a[3]), I(a[4]), C(a[5]), OptB(a, 6));
        r["textWidth"] = a => (double)SoftCanvas.TextWidth(a[0]?.ToString() ?? "", I(a[1]), OptB(a, 2));
        r["textHeight"] = a => (double)SoftCanvas.TextHeight(I(a[0]), OptB(a, 1));
        r["surfaceGetPixel"] = a =>
        {
            var c = S(a[0]).Canvas; int x = I(a[1]), y = I(a[2]);
            return x < 0 || y < 0 || x >= c.Width || y >= c.Height ? -1.0 : (double)c.Pixels[y * c.Width + x];
        };
        // surfaceDrawFrame(s, файл, x, y, w, h) - кадр сцени (frame.raw від
        // програми з NX_STAGE_DIR), вписаний у прямокутник зі збереженням
        // пропорцій. Повертає [ширина кадру, висота кадру] або null.
        r["surfaceDrawFrame"] = a =>
        {
            var frame = StageBackend.LoadFrame(a[1]?.ToString() ?? "");
            if (frame == null) return null;
            int x = I(a[2]), y = I(a[3]), w = I(a[4]), h = I(a[5]);
            double k = Math.Min((double)w / frame.Width, (double)h / frame.Height);
            int dw = (int)(frame.Width * k), dh = (int)(frame.Height * k);
            S(a[0]).Canvas.BlitScaled(frame, x + (w - dw) / 2, y + (h - dh) / 2, dw, dh);
            return new List<object> { (double)frame.Width, (double)frame.Height };
        };
        r["surfaceClip"] = a => { S(a[0]).Canvas.PushClip(I(a[1]), I(a[2]), I(a[3]), I(a[4])); return null; };
        r["surfaceUnclip"] = a => { S(a[0]).Canvas.PopClip(); return null; };

        // surfaceNextEvent(s, чекатиМс?) - подія (мапа) або null. Без другого
        // аргументу чекає, доки подія не прийде (типовий цикл IDE).
        r["surfaceNextEvent"] = a => S(a[0]).NextEvent(OptI(a, 1, -1));
        r["surfaceMouseX"] = a => (double)S(a[0]).MouseX;
        r["surfaceMouseY"] = a => (double)S(a[0]).MouseY;
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
            S(a[0]).Post(e);
            return null;
        };
        r["clipboardGet"] = a => NxClipboard.Get();
        r["clipboardSet"] = a => { NxClipboard.Set(a[0]?.ToString() ?? ""); return null; };
    }

}
