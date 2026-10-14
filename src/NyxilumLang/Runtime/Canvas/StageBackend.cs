namespace NyxilumLang.Runtime.Canvas;

// Полотно-"сцена" для Nx IDE (NX_STAGE_DIR=<тека>): програма працює окремим
// процесом (зависання не вбиває IDE), а її кадри показуються всередині IDE.
// Кадр - <тека>/frame.raw: "NXST", ширина, висота (int32 LE), пікселі
// 0x00RRGGBB (uint32 LE); пишеться у frame.tmp і атомарно перейменовується.
// Події від IDE - рядки, що дописуються в <тека>/input.txt:
//   key <назва> / keyup <назва> / text <символ> / down x y кнопка / up x y кнопка / move x y
public sealed class StageBackend : ISurfaceBackend
{
    private readonly Surface _surface;
    private readonly string _dir;
    private long _inputPos;
    private long _lastFrame;
    private uint[] _last = Array.Empty<uint>();

    public StageBackend(Surface surface, string dir)
    {
        _surface = surface;
        _dir = dir;
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "input.txt");
        if (File.Exists(input)) _inputPos = new FileInfo(input).Length;
    }

    public void Present(SoftCanvas c)
    {
        // не частіше ~60 кадрів/с і лише якщо кадр змінився
        long now = Environment.TickCount64;
        if (now - _lastFrame < 15 && _last.Length == c.Pixels.Length) return;
        if (_last.Length == c.Pixels.Length && c.Pixels.AsSpan().SequenceEqual(_last)) return;
        _lastFrame = now;
        if (_last.Length != c.Pixels.Length) _last = new uint[c.Pixels.Length];
        c.Pixels.CopyTo(_last, 0);
        var tmp = Path.Combine(_dir, "frame.tmp");
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(f))
        {
            w.Write((byte)'N'); w.Write((byte)'X'); w.Write((byte)'S'); w.Write((byte)'T');
            w.Write(c.Width); w.Write(c.Height);
            var bytes = new byte[c.Pixels.Length * 4];
            Buffer.BlockCopy(c.Pixels, 0, bytes, 0, bytes.Length);
            w.Write(bytes);
        }
        File.Move(tmp, Path.Combine(_dir, "frame.raw"), overwrite: true);
    }

    public void Pump()
    {
        var input = Path.Combine(_dir, "input.txt");
        if (!File.Exists(input)) return;
        string text;
        try
        {
            using var f = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (f.Length <= _inputPos) return;
            f.Seek(_inputPos, SeekOrigin.Begin);
            using var r = new StreamReader(f, System.Text.Encoding.UTF8);
            text = r.ReadToEnd();
        }
        catch (IOException) { return; }
        // лише завершені рядки
        int lastNl = text.LastIndexOf('\n');
        if (lastNl < 0) return;
        _inputPos += System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, lastNl + 1));
        foreach (var line in text.Substring(0, lastNl).Split('\n'))
        {
            var p = line.TrimEnd('\r').Split(' ', 2);
            if (p.Length == 0 || p[0] == "") continue;
            var arg = p.Length > 1 ? p[1] : "";
            Dictionary<string, object> e;
            switch (p[0])
            {
                case "key":
                case "keyup":
                    e = Surface.MakeEvent(p[0]);
                    e["key"] = arg;
                    e["ctrl"] = false; e["shift"] = false; e["alt"] = false;
                    break;
                case "text":
                    e = Surface.MakeEvent("text");
                    e["text"] = arg;
                    break;
                case "down":
                case "up":
                case "move":
                    var n = arg.Split(' ');
                    e = Surface.MakeEvent(p[0] == "down" ? "mousedown" : p[0] == "up" ? "mouseup" : "mousemove");
                    e["x"] = double.Parse(n[0], System.Globalization.CultureInfo.InvariantCulture);
                    e["y"] = double.Parse(n[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (n.Length > 2) e["button"] = double.Parse(n[2], System.Globalization.CultureInfo.InvariantCulture);
                    e["ctrl"] = false; e["shift"] = false; e["alt"] = false;
                    break;
                default:
                    continue;
            }
            _surface.Post(e);
        }
    }

    public void SetTitle(string title) { }
    public void Close() { }

    // ---- бік IDE: показати кадр з файла (кеш за часом зміни) ----

    private static readonly Dictionary<string, (DateTime Stamp, SoftCanvas Frame)> _cache = new();

    public static SoftCanvas? LoadFrame(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (_cache.TryGetValue(path, out var c) && c.Stamp == info.LastWriteTimeUtc) return c.Frame;
            var data = File.ReadAllBytes(path);
            if (data.Length < 12 || data[0] != 'N' || data[1] != 'X' || data[2] != 'S' || data[3] != 'T') return null;
            int w = BitConverter.ToInt32(data, 4), h = BitConverter.ToInt32(data, 8);
            if (w <= 0 || h <= 0 || data.Length < 12 + w * h * 4) return null;
            var frame = new SoftCanvas(w, h);
            Buffer.BlockCopy(data, 12, frame.Pixels, 0, w * h * 4);
            _cache[path] = (info.LastWriteTimeUtc, frame);
            return frame;
        }
        catch (IOException) { return _cache.TryGetValue(path, out var c2) ? c2.Frame : null; }
    }
}
