using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using NyxilumLang.Runtime;
using NyxilumLang.VM;

namespace NyxilumLang.Runtime.X11;

// Реєструє guiWindow/guiButton/guiLabel/guiTextBox/guiAdd/guiSetText/
// guiGetText/guiOnAction/guiShow ПОВЕРХ X11Window (Runtime/X11/) - той
// самий набір імен білтінів, що й Windows Forms-версія в VirtualMachine.cs
// (#if WINDOWS), тому один і той самий .nx-скрипт з GUI працює однаково
// на Windows і на Linux/Mac, лише бекенд різний. Реєструється лише коли
// WINDOWS-гілка НЕ зареєструвала ці самі імена (дивись виклик у
// VirtualMachine.cs) - назви ніколи не дублюються для одного нативного
// registry.
public static class X11Gui
{
    // Один "контрол" - і Button, і Label, і TextBox: як і в rawgui,
    // віджети це просто прямокутні області в backbuffer вікна з hit-test
    // проти координат кліку, не окремі X11-підвікна.
    public sealed class Control
    {
        public string Kind = ""; // "button" | "label" | "textbox"
        public int X, Y, W, H;
        public string Text = "";
        public NxFunctionRef? OnClick;
    }

    // Повертається guiWindow() - головний "хендл" вікна, який .nx-скрипт
    // передає в guiAdd/guiShow/... (як Form у Windows Forms-версії).
    public sealed class WindowState
    {
        public X11Window Window = null!;
        public readonly List<Control> Controls = new();
        public readonly ConcurrentQueue<Action> PendingCallbacks = new();
        public readonly ManualResetEventSlim Activity = new(false);
        public volatile bool Closed;
        public Control? MouseDownControl;
    }

    public static void Register(Dictionary<string, Func<object[], object?>> registry)
    {
        registry["guiWindow"] = args =>
        {
            var state = new WindowState();
            state.Window = X11Window.Create(width: Convert.ToInt32(args[1]), height: Convert.ToInt32(args[2]), title: args[0]?.ToString());
            state.Window.OnExpose += () => Redraw(state);
            state.Window.OnResize += (_, _) => Redraw(state);
            state.Window.OnMouseDown += evt => state.MouseDownControl = HitTest(state, evt.EventX, evt.EventY);
            state.Window.OnMouseUp += evt =>
            {
                var c = HitTest(state, evt.EventX, evt.EventY);
                if (c != null && c == state.MouseDownControl && c.OnClick != null)
                {
                    var funcRef = c.OnClick;
                    state.PendingCallbacks.Enqueue(() => VirtualMachine.Current!.InvokeFunctionValue(funcRef, Array.Empty<object>()));
                    state.Activity.Set();
                }
                state.MouseDownControl = null;
            };
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

        // Блокує, доки вікно не закриють (як у Windows Forms Application.Run) -
        // на відміну від JS-версії (Promise/подія), тут звичайний
        // ManualResetEventSlim-цикл: клікабельні колбеки, що приходять з
        // ФОНОВОГО потоку X11Client (Runtime/X11/X11Client.cs, Loop()),
        // лише СТАВЛЯТЬСЯ В ЧЕРГУ там, а виконуються тут, на потоці виклику
        // guiShow - той самий потік, що виконує решту .nx-скрипта, тож VM
        // ніколи не викликається одночасно з двох потоків.
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
                // Колбек типово міняє текст через guiSetText - те саме, що й
                // Windows Forms-версія робить автоматично (нативний Control
                // сам перемальовується при зміні .Text): тут перемальовка не
                // автоматична (Control - лише дані, не справжній X11-
                // ресурс), тож без цього виклику клік технічно спрацьовує
                // (funcRef викликається), але екран лишається старим.
                if (ranAny && !state.Closed) Redraw(state);
                if (state.Closed) break;
            }
            return null;
        };
    }

    private static Control? HitTest(WindowState state, int x, int y)
    {
        // У зворотному порядку додавання - останній доданий (візуально
        // "зверху", якщо колись з'являться накладені контроли) виграє.
        for (int i = state.Controls.Count - 1; i >= 0; i--)
        {
            var c = state.Controls[i];
            if (c.Kind == "button" && x >= c.X && x < c.X + c.W && y >= c.Y && y < c.Y + c.H) return c;
        }
        return null;
    }

    private static void Redraw(WindowState state)
    {
        var win = state.Window;
        win.Clear();
        foreach (var c in state.Controls)
        {
            switch (c.Kind)
            {
                case "label":
                    win.DrawText(c.Text, c.X, c.Y, 2, 0xe4e4e7);
                    break;
                case "button":
                    win.FillRect(c.X, c.Y, c.W, c.H, 0x2d2d35);
                    win.DrawText(c.Text, c.X + 8, c.Y + Math.Max(0, (c.H - Font5x7.GlyphHeight * 2) / 2), 2, 0xffffff);
                    break;
                case "textbox":
                    win.FillRect(c.X, c.Y, c.W, c.H, 0x0e0e11);
                    win.DrawText(c.Text, c.X + 6, c.Y + Math.Max(0, (c.H - Font5x7.GlyphHeight * 2) / 2), 2, 0xe4e4e7);
                    break;
            }
        }
        win.Repaint();
    }
}
