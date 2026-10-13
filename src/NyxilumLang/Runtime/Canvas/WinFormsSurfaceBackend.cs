#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace NyxilumLang.Runtime.Canvas;

// Windows: Form з подвійною буферизацією, пікселі SoftCanvas копіюються в
// Bitmap (LockBits) і малюються в OnPaint. Повідомлення Windows обробляються
// на потоці VM через Pump() (Application.DoEvents) - так само, як
// presentCanvas у GraphicsModule.
public sealed class WinFormsSurfaceBackend : ISurfaceBackend
{
    private sealed class SurfaceForm : Form
    {
        public Bitmap? Frame;
        public SurfaceForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            KeyPreview = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Frame != null) e.Graphics.DrawImageUnscaled(Frame, 0, 0);
        }
        // Стрілки/Tab інакше з'їдає навігація між контролами форми.
        protected override bool IsInputKey(Keys keyData) => true;
        protected override bool ProcessDialogKey(Keys keyData) => false;
    }

    private readonly SurfaceForm _form;
    private readonly Surface _surface;

    public WinFormsSurfaceBackend(Surface surface, string title, int width, int height)
    {
        _surface = surface;
        _form = new SurfaceForm { Text = title, ClientSize = new Size(width, height), StartPosition = FormStartPosition.CenterScreen };

        _form.MouseDown += (s, e) => PostButton("mousedown", e);
        _form.MouseUp += (s, e) => PostButton("mouseup", e);
        _form.MouseMove += (s, e) =>
        {
            var ev = Surface.MakeEvent("mousemove");
            AddMouse(ev, e.X, e.Y);
            _surface.Post(ev);
        };
        _form.MouseWheel += (s, e) =>
        {
            var ev = Surface.MakeEvent("wheel");
            AddMouse(ev, e.X, e.Y);
            ev["dy"] = e.Delta > 0 ? -1.0 : 1.0;
            ev["dx"] = 0.0;
            _surface.Post(ev);
        };
        _form.KeyDown += (s, e) => PostKey("key", e);
        _form.KeyUp += (s, e) => PostKey("keyup", e);
        _form.KeyPress += (s, e) =>
        {
            if (char.IsControl(e.KeyChar) || (Control.ModifierKeys & (Keys.Control | Keys.Alt)) == Keys.Control) return;
            var ev = Surface.MakeEvent("text");
            ev["text"] = e.KeyChar.ToString();
            _surface.Post(ev);
        };
        _form.Resize += (s, e) =>
        {
            if (_form.ClientSize.Width <= 0 || _form.ClientSize.Height <= 0) return;
            var ev = Surface.MakeEvent("resize");
            ev["width"] = (double)_form.ClientSize.Width;
            ev["height"] = (double)_form.ClientSize.Height;
            _surface.Post(ev);
        };
        _form.FormClosed += (s, e) => _surface.Post(Surface.MakeEvent("close"));

        _form.Show();
        _form.Activate();
    }

    private static void AddMods(Dictionary<string, object> ev)
    {
        var m = Control.ModifierKeys;
        ev["shift"] = (m & Keys.Shift) != 0;
        ev["ctrl"] = (m & Keys.Control) != 0;
        ev["alt"] = (m & Keys.Alt) != 0;
    }

    private static void AddMouse(Dictionary<string, object> ev, int x, int y)
    {
        ev["x"] = (double)x;
        ev["y"] = (double)y;
        AddMods(ev);
    }

    private void PostButton(string type, MouseEventArgs e)
    {
        var ev = Surface.MakeEvent(type);
        AddMouse(ev, e.X, e.Y);
        ev["button"] = e.Button switch { MouseButtons.Left => 1.0, MouseButtons.Middle => 2.0, MouseButtons.Right => 3.0, _ => 0.0 };
        _surface.Post(ev);
    }

    private void PostKey(string type, KeyEventArgs e)
    {
        string? key = KeyName(e.KeyCode);
        if (key == null) return;
        var ev = Surface.MakeEvent(type);
        ev["key"] = key;
        AddMods(ev);
        _surface.Post(ev);
    }

    // Ті самі назви, що й у X11SurfaceBackend.
    private static string? KeyName(Keys k)
    {
        if (k >= Keys.A && k <= Keys.Z) return ((char)('a' + (k - Keys.A))).ToString();
        if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
        if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return ((char)('0' + (k - Keys.NumPad0))).ToString();
        if (k >= Keys.F1 && k <= Keys.F12) return "F" + (k - Keys.F1 + 1);
        return k switch
        {
            Keys.Back => "Backspace", Keys.Tab => "Tab", Keys.Enter => "Enter", Keys.Escape => "Escape",
            Keys.Delete => "Delete", Keys.Insert => "Insert", Keys.Left => "Left", Keys.Up => "Up",
            Keys.Right => "Right", Keys.Down => "Down", Keys.Home => "Home", Keys.End => "End",
            Keys.PageUp => "PageUp", Keys.PageDown => "PageDown", Keys.Space => "Space",
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => "Shift",
            Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => "Control",
            Keys.Menu or Keys.LMenu or Keys.RMenu => "Alt", Keys.LWin or Keys.RWin => "Super",
            Keys.CapsLock => "CapsLock",
            Keys.OemMinus => "-", Keys.Oemplus => "=", Keys.OemOpenBrackets => "[", Keys.OemCloseBrackets => "]",
            Keys.OemSemicolon => ";", Keys.OemQuotes => "'", Keys.Oemcomma => ",", Keys.OemPeriod => ".",
            Keys.OemQuestion => "/", Keys.OemPipe => "\\", Keys.Oemtilde => "`",
            Keys.Add => "+", Keys.Subtract => "-", Keys.Multiply => "*", Keys.Divide => "/", Keys.Decimal => ".",
            _ => null
        };
    }

    private int[] _copy = Array.Empty<int>();

    public void Present(SoftCanvas c)
    {
        if (_form.IsDisposed) return;
        GuiThreadGuard.Ensure(_form);
        if (_form.Frame == null || _form.Frame.Width != c.Width || _form.Frame.Height != c.Height)
        {
            _form.Frame?.Dispose();
            _form.Frame = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppRgb);
        }
        var bmp = _form.Frame;
        var data = bmp.LockBits(new Rectangle(0, 0, c.Width, c.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            if (_copy.Length != c.Pixels.Length) _copy = new int[c.Pixels.Length];
            Buffer.BlockCopy(c.Pixels, 0, _copy, 0, c.Pixels.Length * 4);
            for (int y = 0; y < c.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(_copy, y * c.Width, data.Scan0 + y * data.Stride, c.Width);
        }
        finally { bmp.UnlockBits(data); }
        _form.Invalidate();
        _form.Update();
        Application.DoEvents();
    }

    public void SetTitle(string title) { if (!_form.IsDisposed) _form.Text = title; }
    public void Close() { if (!_form.IsDisposed) _form.Close(); }
    public void Pump() { if (!_form.IsDisposed) Application.DoEvents(); }
}
#endif
