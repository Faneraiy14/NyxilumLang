using System.Collections.Concurrent;

namespace NyxilumLang.Runtime.Canvas;

// Платформна частина вікна-полотна: показати готові пікселі й постачати
// події. Реалізації: X11SurfaceBackend (Linux/Mac), WinFormsSurfaceBackend
// (Windows).
public interface ISurfaceBackend
{
    void Present(SoftCanvas canvas);
    void SetTitle(string title);
    void Close();
    // Windows Forms обробляє повідомлення лише на своєму потоці - Pump()
    // викликається з потоку VM під час очікування подій. X11 читає сокет
    // у фоновому потоці, тож там Pump() нічого не робить.
    void Pump();
}

public sealed class HeadlessSurfaceBackend : ISurfaceBackend
{
    public void Present(SoftCanvas canvas) { }
    public void SetTitle(string title) { }
    public void Close() { }
    public void Pump() { }
}

// Вікно-полотно surface*: SoftCanvas + черга подій + стан миші/клавіш для
// опитування (ігровий цикл: surfaceKeyDown/surfaceMouseX).
//
// Подія - Dictionary<string, object> з полями (читаються через крапку: e.type, e.x):
//   type: "mousedown" | "mouseup" | "mousemove" | "wheel" | "key" | "keyup"
//         | "text" | "resize" | "close"
//   x, y          - координати миші (усі події миші; wheel теж)
//   button        - 1 ліва, 2 середня, 3 права (mousedown/mouseup)
//   dy            - wheel: -1 вгору, +1 вниз (dx - горизонтальне колесо)
//   key           - key/keyup: назва клавіші ("a", "1", "Enter", "Left", ...;
//                   для літер - латинська літера ФІЗИЧНОЇ клавіші навіть в
//                   українській розкладці, щоб Ctrl+C працював завжди)
//   text          - "text": набраний символ з урахуванням розкладки ("ї")
//   ctrl, shift, alt - модифікатори (bool)
//   width, height - resize
//   time          - мілісекунди (Environment.TickCount64), для подвійного кліку
public sealed class Surface
{
    public SoftCanvas Canvas;
    public ISurfaceBackend Backend = null!;
    public volatile bool Closed;

    private readonly ConcurrentQueue<Dictionary<string, object>> _events = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public volatile int MouseX, MouseY;
    public volatile bool MouseDown;
    private readonly ConcurrentDictionary<string, bool> _keysDown = new(StringComparer.OrdinalIgnoreCase);

    public Surface(int width, int height) { Canvas = new SoftCanvas(width, height); }

    public static Dictionary<string, object> MakeEvent(string type)
    {
        var m = new Dictionary<string, object>();
        m["type"] = type;
        m["time"] = (double)Environment.TickCount64;
        return m;
    }

    // Викликається з потоку платформи (X11 reader / WinForms-обробник).
    public void Post(Dictionary<string, object> evt)
    {
        var type = (string)evt["type"];
        if (evt.TryGetValue("x", out var x)) MouseX = (int)(double)x;
        if (evt.TryGetValue("y", out var y)) MouseY = (int)(double)y;
        if (type == "mousedown" && evt.TryGetValue("button", out var b) && (double)b == 1) MouseDown = true;
        if (type == "mouseup" && evt.TryGetValue("button", out var b2) && (double)b2 == 1) MouseDown = false;
        if (type == "key") _keysDown[(string)evt["key"]] = true;
        if (type == "keyup") _keysDown.TryRemove((string)evt["key"], out _);
        if (type == "close") Closed = true;

        // Рух миші приходить десятками за кадр - якщо попередній mousemove
        // ще не забрали, він просто застарів. Лишаємо лише останній, щоб IDE
        // не відставала від курсора при перетягуванні блоків.
        lock (_moveLock)
        {
            if (type == "mousemove" && _lastMove != null && !_lastMoveTaken)
            {
                _lastMove["x"] = evt["x"];
                _lastMove["y"] = evt["y"];
                _lastMove["time"] = evt["time"];
                return;
            }
            if (type == "mousemove") { _lastMove = evt; _lastMoveTaken = false; }
            _events.Enqueue(evt);
        }
        _signal.Release();
    }
    private readonly object _moveLock = new();
    private Dictionary<string, object>? _lastMove;
    private bool _lastMoveTaken = true;

    public bool KeyDown(string name) => _keysDown.ContainsKey(name);

    // Наступна подія або null. waitMs: 0 - не чекати, < 0 - чекати безкінечно.
    public Dictionary<string, object>? NextEvent(int waitMs)
    {
        var deadline = waitMs < 0 ? long.MaxValue : Environment.TickCount64 + waitMs;
        while (true)
        {
            Backend.Pump();
            Dictionary<string, object>? evt;
            lock (_moveLock)
            {
                if (_events.TryDequeue(out evt) && ReferenceEquals(evt, _lastMove)) _lastMoveTaken = true;
            }
            if (evt != null)
            {
                if ((string)evt["type"] == "resize")
                    Canvas.Resize((int)(double)evt["width"], (int)(double)evt["height"]);
                return evt;
            }
            if (Closed) return null;
            long left = deadline - Environment.TickCount64;
            if (left <= 0) return null;
            // Windows Forms потребує регулярного Pump() - тому короткі
            // відрізки очікування, а не одне довге Wait.
            _signal.Wait((int)Math.Min(left, 15));
        }
    }
}
