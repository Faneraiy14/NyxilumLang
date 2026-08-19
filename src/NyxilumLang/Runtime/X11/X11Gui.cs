using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using NyxilumLang.Runtime;
using NyxilumLang.VM;

namespace NyxilumLang.Runtime.X11;

// Реєструє guiWindow/guiButton/guiLabel/guiTextBox/guiCheckbox/guiDropdown/
// guiScrollList/guiProgressBar/guiEntry/... ПОВЕРХ X11Window (Runtime/X11/) -
// портовано з набору віджетів rawgui (button.js/checkbox.js/dropdown.js/
// scroll-list.js/progress-bar.js/entry.js), той самий принцип: кожен
// віджет - прямокутна область у backbuffer батьківського вікна з hit-test
// проти координат кліку, БЕЗ окремих X11-підвікон на елемент.
public static class X11Gui
{
    public sealed class Control
    {
        public string Kind = ""; // "button" | "label" | "textbox" | "checkbox" | "dropdown" | "scrolllist" | "progressbar" | "entry"
        public int X, Y, W, H;
        public string Text = "";
        public NxFunctionRef? OnClick;

        // checkbox
        public bool Checked;
        // dropdown / scrolllist
        public List<string> Options = new();
        public int SelectedIndex = -1;
        public bool IsOpen;
        public int ScrollOffset;
        public const int ItemHeight = 28;
        // progressbar (0-100)
        public double Progress;
        // entry
        public string Placeholder = "";
        public bool Focused;
    }

    public sealed class WindowState
    {
        public X11Window Window = null!;
        public readonly List<Control> Controls = new();
        public readonly ConcurrentQueue<Action> PendingCallbacks = new();
        public readonly ManualResetEventSlim Activity = new(false);
        public volatile bool Closed;
        public Control? MouseDownControl;
        public Control? FocusedEntry;
        public Keymap? Keymap;
    }

    public static void Register(Dictionary<string, Func<object[], object?>> registry)
    {
        registry["guiWindow"] = args =>
        {
            Sandbox.CheckGui();
            var state = new WindowState();
            state.Window = X11Window.Create(width: Convert.ToInt32(args[1]), height: Convert.ToInt32(args[2]), title: args[0]?.ToString());
            state.Keymap = Keymap.Load(state.Window.Client, state.Window.Client.Conn);
            state.Window.OnExpose += () => Redraw(state);
            state.Window.OnResize += (_, _) => Redraw(state);
            state.Window.OnMouseDown += evt => OnMouseDown(state, evt);
            state.Window.OnMouseUp += evt => OnMouseUp(state, evt);
            state.Window.OnKey += evt => OnKey(state, evt);
            state.Window.OnClose += () => { state.Closed = true; state.Activity.Set(); };
            state.Window.OnDeleteRequest += () => { state.Closed = true; state.Activity.Set(); };
            return state;
        };

        registry["guiButton"] = args => new Control
        {
            Kind = "button", Text = args[0]?.ToString() ?? "",
            X = Convert.ToInt32(args[1]), Y = Convert.ToInt32(args[2]), W = Convert.ToInt32(args[3]), H = Convert.ToInt32(args[4])
        };
        registry["guiLabel"] = args => new Control
        {
            Kind = "label", Text = args[0]?.ToString() ?? "",
            X = Convert.ToInt32(args[1]), Y = Convert.ToInt32(args[2]), W = Convert.ToInt32(args[3]), H = Convert.ToInt32(args[4])
        };
        registry["guiTextBox"] = args => new Control
        {
            Kind = "textbox",
            X = Convert.ToInt32(args[0]), Y = Convert.ToInt32(args[1]), W = Convert.ToInt32(args[2]), H = Convert.ToInt32(args[3])
        };

        // guiCheckbox(підпис, x, y, size?) - квадратик 18x18 за замовчуванням,
        // висота хіт-тесту й розмальовки дорівнює size.
        registry["guiCheckbox"] = args =>
        {
            int size = args.Length > 3 ? Convert.ToInt32(args[3]) : 18;
            return new Control { Kind = "checkbox", Text = args[0]?.ToString() ?? "", X = Convert.ToInt32(args[1]), Y = Convert.ToInt32(args[2]), W = size, H = size };
        };

        // guiDropdown(x, y, width, опції[], висота?) - опції: масив
        // NyxilumLang-значень (toString() кожного - те, що показується).
        registry["guiDropdown"] = args =>
        {
            int height = args.Length > 4 ? Convert.ToInt32(args[4]) : 36;
            var control = new Control { Kind = "dropdown", X = Convert.ToInt32(args[0]), Y = Convert.ToInt32(args[1]), W = Convert.ToInt32(args[2]), H = height };
            if (args[3] is List<object> opts) foreach (var o in opts) control.Options.Add(o?.ToString() ?? "");
            if (control.Options.Count > 0) control.SelectedIndex = 0;
            return control;
        };

        registry["guiScrollList"] = args => new Control
        {
            Kind = "scrolllist", X = Convert.ToInt32(args[0]), Y = Convert.ToInt32(args[1]), W = Convert.ToInt32(args[2]), H = Convert.ToInt32(args[3])
        };

        registry["guiProgressBar"] = args => new Control
        {
            Kind = "progressbar", X = Convert.ToInt32(args[0]), Y = Convert.ToInt32(args[1]), W = Convert.ToInt32(args[2]), H = Convert.ToInt32(args[3])
        };

        // guiEntry(x, y, width, height, підказка?) - на відміну від
        // guiTextBox (лише показ, ReadOnly - як у Windows Forms-версії),
        // редагується з клавіатури після кліку (фокус - прикладний стан,
        // не справжній X11 input-фокус; усі клавіші й так ідуть у вікно
        // цілком, маршрутизація "яке поле активне" - наша відповідальність).
        registry["guiEntry"] = args => new Control
        {
            Kind = "entry", X = Convert.ToInt32(args[0]), Y = Convert.ToInt32(args[1]), W = Convert.ToInt32(args[2]), H = Convert.ToInt32(args[3]),
            Placeholder = args.Length > 4 ? args[4]?.ToString() ?? "" : ""
        };

        registry["guiAdd"] = args =>
        {
            var parent = (WindowState)args[0];
            var child = (Control)args[1];
            parent.Controls.Add(child);
            return null;
        };

        registry["guiSetText"] = args =>
        {
            var control = (Control)args[0];
            control.Text = args[1]?.ToString() ?? "";
            return null;
        };
        registry["guiGetText"] = args => ((Control)args[0]).Text;

        registry["guiOnAction"] = args =>
        {
            var control = (Control)args[0];
            control.OnClick = (NxFunctionRef)args[1];
            return null;
        };

        // checkbox
        registry["guiGetChecked"] = args => ((Control)args[0]).Checked;
        registry["guiSetChecked"] = args => { ((Control)args[0]).Checked = ToBool(args[1]); return null; };

        // dropdown / scrolllist - спільні: опції/елементи, вибір за індексом
        // чи за поточним значенням.
        registry["guiSetOptions"] = args =>
        {
            var c = (Control)args[0];
            c.Options.Clear();
            if (args[1] is List<object> opts) foreach (var o in opts) c.Options.Add(o?.ToString() ?? "");
            c.SelectedIndex = c.Options.Count > 0 ? 0 : -1;
            c.ScrollOffset = 0;
            return null;
        };
        registry["guiGetSelected"] = args =>
        {
            var c = (Control)args[0];
            return c.SelectedIndex >= 0 && c.SelectedIndex < c.Options.Count ? c.Options[c.SelectedIndex] : null;
        };
        registry["guiSetSelected"] = args =>
        {
            var c = (Control)args[0];
            var target = args[1]?.ToString() ?? "";
            var idx = c.Options.IndexOf(target);
            if (idx >= 0) c.SelectedIndex = idx;
            return idx >= 0;
        };

        // progressbar
        registry["guiSetProgress"] = args =>
        {
            var c = (Control)args[0];
            c.Progress = Math.Max(0, Math.Min(100, Convert.ToDouble(args[1])));
            return null;
        };

        // Блокує, доки вікно не закриють (як у Windows Forms Application.Run) -
        // клікабельні/зміноздатні колбеки, що приходять з ФОНОВОГО потоку
        // X11Client (Runtime/X11/X11Client.cs, Loop()), лише СТАВЛЯТЬСЯ В
        // ЧЕРГУ там, а виконуються тут, на потоці виклику guiShow - тому
        // VM ніколи не викликається одночасно з двох потоків.
        registry["guiShow"] = args =>
        {
            var state = (WindowState)args[0];
            Redraw(state);
            state.Window.Repaint();
            while (true)
            {
                state.Activity.Wait();
                state.Activity.Reset();
                bool ranAny = false;
                while (state.PendingCallbacks.TryDequeue(out var cb)) { cb(); ranAny = true; }
                if (ranAny && !state.Closed) Redraw(state);
                if (state.Closed) break;
            }
            return null;
        };
    }

    private static bool ToBool(object? v) => v switch { bool b => b, double d => d != 0, _ => v != null };

    private static void QueueCallback(WindowState state, NxFunctionRef? funcRef)
    {
        if (funcRef == null) return;
        state.PendingCallbacks.Enqueue(() => VirtualMachine.Current!.InvokeFunctionValue(funcRef, Array.Empty<object>()));
        state.Activity.Set();
    }

    // Топ-міст контрол (останній доданий), у чиї межі потрапляє точка.
    private static Control? HitTestAny(WindowState state, int x, int y)
    {
        for (int i = state.Controls.Count - 1; i >= 0; i--)
        {
            var c = state.Controls[i];
            if (c.Kind is "label" or "textbox") continue; // не клікабельні
            if (x >= c.X && x < c.X + c.W && y >= c.Y && y < c.Y + c.H) return c;
        }
        return null;
    }

    private static void OnMouseDown(WindowState state, X11Event evt)
    {
        // Відкритий дропдаун перехоплює клік ПЕРШИМ - його overlay виходить
        // за межі власних задекларованих X/Y/W/H (rawgui/dropdown.js).
        Control? openDropdown = null;
        foreach (var c in state.Controls) if (c.Kind == "dropdown" && c.IsOpen) { openDropdown = c; break; }
        if (openDropdown != null)
        {
            int idx = DropdownOptionIndexAt(openDropdown, evt.EventX, evt.EventY);
            if (idx >= 0)
            {
                openDropdown.SelectedIndex = idx;
                QueueCallback(state, openDropdown.OnClick);
            }
            openDropdown.IsOpen = false;
            Redraw(state);
            return;
        }

        var hit = HitTestAny(state, evt.EventX, evt.EventY);
        if (hit == null)
        {
            if (state.FocusedEntry != null) { state.FocusedEntry.Focused = false; state.FocusedEntry = null; Redraw(state); }
            return;
        }

        switch (hit.Kind)
        {
            case "button":
                state.MouseDownControl = hit;
                break;
            case "checkbox":
                hit.Checked = !hit.Checked;
                Redraw(state);
                QueueCallback(state, hit.OnClick);
                break;
            case "dropdown":
                hit.IsOpen = true;
                Redraw(state);
                break;
            case "scrolllist":
                if (evt.Detail == 4) hit.ScrollOffset = Math.Max(0, hit.ScrollOffset - 1);
                else if (evt.Detail == 5) hit.ScrollOffset = Math.Min(ScrollListMaxOffset(hit), hit.ScrollOffset + 1);
                else if (evt.Detail == 1)
                {
                    int visibleCount = Math.Max(1, hit.H / Control.ItemHeight);
                    int row = (evt.EventY - hit.Y) / Control.ItemHeight;
                    int idx = hit.ScrollOffset + row;
                    if (idx >= 0 && idx < hit.Options.Count)
                    {
                        hit.SelectedIndex = idx;
                        QueueCallback(state, hit.OnClick);
                    }
                }
                Redraw(state);
                break;
            case "entry":
                if (state.FocusedEntry != null && state.FocusedEntry != hit) state.FocusedEntry.Focused = false;
                hit.Focused = true;
                state.FocusedEntry = hit;
                Redraw(state);
                break;
        }
    }

    private static void OnMouseUp(WindowState state, X11Event evt)
    {
        var c = HitTestAny(state, evt.EventX, evt.EventY);
        if (c != null && c == state.MouseDownControl && c.Kind == "button")
            QueueCallback(state, c.OnClick);
        state.MouseDownControl = null;
    }

    private static void OnKey(WindowState state, X11Event evt)
    {
        var entry = state.FocusedEntry;
        if (entry == null || state.Keymap == null) return;
        bool shift = (evt.State & 0x1) != 0;
        var ch = state.Keymap.KeycodeToChar(evt.Detail, shift);
        if (ch == null) return;

        if (ch == "BackSpace")
        {
            if (entry.Text.Length > 0) entry.Text = entry.Text[..^1];
        }
        else if (ch == "Return")
        {
            QueueCallback(state, entry.OnClick); // submit - той самий funcRef, що й guiOnAction
            return;
        }
        else if (ch.Length == 1)
        {
            entry.Text += ch;
        }
        else return; // інші спецклавіші (Tab/стрілки/...) поки не оброблені
        Redraw(state);
    }

    private static int ScrollListMaxOffset(Control c)
    {
        int visibleCount = Math.Max(1, c.H / Control.ItemHeight);
        return Math.Max(0, c.Options.Count - visibleCount);
    }

    private static int DropdownOptionIndexAt(Control dd, int x, int y)
    {
        int bx = dd.X, by = dd.Y + dd.H, bw = dd.W, bh = dd.Options.Count * Control.ItemHeight;
        if (x < bx || x >= bx + bw || y < by || y >= by + bh) return -1;
        return (y - by) / Control.ItemHeight;
    }

    private static void Redraw(WindowState state)
    {
        var win = state.Window;
        win.Clear();
        foreach (var c in state.Controls) DrawControl(win, c);
        // Дропдауни малюються ОСТАННІМИ (поверх усього іншого) - overlay
        // відкритого списку має перекривати сусідні віджети під ним, а не
        // навпаки; backbuffer плаский (без z-order), тож порядок малювання
        // сам визначає, що "зверху".
        foreach (var c in state.Controls) if (c.Kind == "dropdown" && c.IsOpen) DrawDropdownOverlay(win, c);
        win.Repaint();
    }

    private static void DrawControl(X11Window win, Control c)
    {
        switch (c.Kind)
        {
            case "label":
                win.DrawText(c.Text, c.X, c.Y, 2, 0xe4e4e7);
                break;
            case "button":
                win.FillRect(c.X, c.Y, c.W, c.H, 0x2d2d35);
                {
                    var (tw, th) = TextRender.MeasureText(c.Text, 2);
                    win.DrawText(c.Text, c.X + Math.Max(0, (c.W - tw) / 2), c.Y + Math.Max(0, (c.H - th) / 2), 2, 0xffffff);
                }
                break;
            case "textbox":
                win.FillRect(c.X, c.Y, c.W, c.H, 0x0e0e11);
                win.DrawText(c.Text, c.X + 6, c.Y + Math.Max(0, (c.H - Font5x7.GlyphHeight * 2) / 2), 2, 0xe4e4e7);
                break;
            case "checkbox":
                win.FillRect(c.X, c.Y, c.W, c.H, 0x5a5a6e);
                win.FillRect(c.X + 2, c.Y + 2, c.W - 4, c.H - 4, 0x2d2d3a);
                if (c.Checked)
                {
                    int pad = (int)Math.Round(c.W * 0.22);
                    win.DrawLines(new[]
                    {
                        new X11Protocol.Point((short)(c.X + pad), (short)(c.Y + c.H / 2)),
                        new X11Protocol.Point((short)(c.X + c.W / 2), (short)(c.Y + c.H - pad)),
                        new X11Protocol.Point((short)(c.X + c.W - pad), (short)(c.Y + pad))
                    }, 0x2fa35c);
                }
                if (!string.IsNullOrEmpty(c.Text))
                    win.DrawText(c.Text, c.X + c.W + 8, c.Y + Math.Max(0, (c.H - Font5x7.GlyphHeight * 2) / 2), 2, 0xffffff);
                break;
            case "dropdown":
                win.FillRect(c.X, c.Y, c.W, c.H, 0x5a5a6e);
                win.FillRect(c.X + 1, c.Y + 1, c.W - 2, c.H - 2, 0x2d2d3a);
                {
                    var label = c.SelectedIndex >= 0 && c.SelectedIndex < c.Options.Count ? c.Options[c.SelectedIndex] : "";
                    win.DrawText(label, c.X + 8, c.Y + Math.Max(0, (c.H - Font5x7.GlyphHeight * 2) / 2), 2, 0xffffff);
                    int arrowX = c.X + c.W - 20, arrowY = c.Y + c.H / 2 - 2;
                    win.FillRect(arrowX, arrowY, 8, 2, 0xffffff);
                    win.FillRect(arrowX + 2, arrowY + 2, 4, 2, 0xffffff);
                }
                break;
            case "scrolllist":
                win.FillRect(c.X, c.Y, c.W, c.H, 0x14141a);
                {
                    int visibleCount = Math.Max(1, c.H / Control.ItemHeight);
                    for (int row = 0; row < visibleCount; row++)
                    {
                        int idx = c.ScrollOffset + row;
                        if (idx >= c.Options.Count) break;
                        int itemY = c.Y + row * Control.ItemHeight;
                        if (idx == c.SelectedIndex) win.FillRect(c.X, itemY, c.W, Control.ItemHeight, 0x2d5a8a);
                        win.DrawText(c.Options[idx], c.X + 8, itemY + Math.Max(0, (Control.ItemHeight - Font5x7.GlyphHeight * 2) / 2), 2, 0xffffff);
                    }
                }
                break;
            case "progressbar":
                win.FillRect(c.X, c.Y, c.W, c.H, 0x5a5a6e);
                win.FillRect(c.X + 1, c.Y + 1, c.W - 2, c.H - 2, 0x2d2d3a);
                {
                    int filled = (int)Math.Round((c.W - 2) * (c.Progress / 100.0));
                    if (filled > 0) win.FillRect(c.X + 1, c.Y + 1, filled, c.H - 2, 0x2fa35c);
                }
                break;
            case "entry":
                win.FillRect(c.X, c.Y, c.W, c.H, c.Focused ? 0x5a8ac6u : 0x3a3a4au);
                win.FillRect(c.X + 2, c.Y + 2, c.W - 4, c.H - 4, 0x14141a);
                {
                    var shown = c.Text.Length > 0 ? c.Text : c.Placeholder;
                    uint color = c.Text.Length > 0 ? 0xffffffu : 0x666677u;
                    win.DrawText(shown, c.X + 8, c.Y + Math.Max(0, (c.H - Font5x7.GlyphHeight * 2) / 2), 2, color);
                }
                break;
        }
    }

    private static void DrawDropdownOverlay(X11Window win, Control c)
    {
        int bx = c.X, by = c.Y + c.H, bw = c.W, bh = c.Options.Count * Control.ItemHeight;
        win.FillRect(bx, by, bw, bh, 0x24243a);
        for (int i = 0; i < c.Options.Count; i++)
        {
            int itemY = by + i * Control.ItemHeight;
            if (i == c.SelectedIndex) win.FillRect(bx, itemY, bw, Control.ItemHeight, 0x2d5a8a);
            win.DrawText(c.Options[i], bx + 8, itemY + Math.Max(0, (Control.ItemHeight - Font5x7.GlyphHeight * 2) / 2), 2, 0xffffff);
        }
    }
}
